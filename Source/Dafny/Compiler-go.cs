//-----------------------------------------------------------------------------
//
// Copyright (C) Amazon.  All Rights Reserved.
//
//-----------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Diagnostics.Contracts;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Bpl = Microsoft.Boogie;

namespace Microsoft.Dafny {
  public class GoCompiler : Compiler {
    public GoCompiler(ErrorReporter reporter)
    : base(reporter) {
    }

    public override string TargetLanguage => "Go";

    protected override void EmitHeader(Program program, TargetWriter wr) {
      wr.WriteLine("// Dafny program {0} compiled into Go", program.Name);
      ReadRuntimeSystem("DafnyRuntime.go", wr);
    }

    public override void EmitCallToMain(Method mainMethod, TextWriter wr) {
      wr.WriteLine("func main() {{ {0}() }}", IdName(mainMethod));
    }
      
    TargetWriter CreateDescribedSection(string desc, TargetWriter wr, params object[] args) {
      var body = wr.NewSection();
      var str = string.Format(desc, args);
      body.WriteLine("// Definition of {0}", str);
      wr.WriteLine("// End of {0}", str);
      return body;
    }

    public override BlockTargetWriter CreateStaticMain(TargetWriter wr) {
      wr.Indent();
      return wr.NewBlock("func main()");
    }

    protected override TargetWriter CreateModule(string moduleName, bool isExtern, string/*?*/ libraryName, TargetWriter wr) {
      return CreateDescribedSection("module {0}", wr, moduleName);
    }

    protected override string GetHelperModuleName() => "_dafny";

    protected override TargetWriter CreateClass(string name, bool isExtern, string/*?*/ fullPrintName, List<TypeParameter>/*?*/ typeParameters, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, TargetWriter wr) {
      wr.Indent();
      var w = CreateDescribedSection("class {0}", wr, name);
      
      var ifw = w.NewBlock(string.Format("type {0} struct", name), "");
      instanceFieldsWriter = ifw;
      if (typeParameters != null) {
        WriteRuntimeTypeDescriptorsFields(typeParameters, false, ifw);
      }

      if (typeParameters != null) {
        foreach (var tp in typeParameters) {
          if (tp.Characteristics.MustSupportZeroInitialization) {
            instanceFieldsWriter.Indent();
            instanceFieldsWriter.WriteLine("this.{0} = {0};", "rtd$_" + tp.CompileName);
          }
        }
      }

      wr.Indent();
      wr.WriteLine("var _ {0} // suppress unused error", name);
      return w;
    }

    protected override BlockTargetWriter CreateTrait(string name, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, out TargetWriter staticMemberWriter, TargetWriter wr) {
      wr.Indent();
      var w = wr.NewBlock(string.Format("type {0} interface", IdProtect(name)), ";");
      w.Indent();
      instanceFieldsWriter = w;
      staticMemberWriter = wr;
      return w;
    }

    protected override BlockTargetWriter CreateIterator(IteratorDecl iter, TargetWriter wr) {
      // An iterator is compiled as follows:
      //   public class MyIteratorExample
      //   {
      //     public T q;  // in-parameter
      //     public T x;  // yield-parameter
      //     public int y;  // yield-parameter
      //     IEnumerator<object> _iter;
      //
      //     public void _MyIteratorExample(T q) {
      //       this.q = q;
      //       _iter = TheIterator();
      //     }
      //
      //     public void MoveNext(out bool more) {
      //       more =_iter.MoveNext();
      //     }
      //
      //     private IEnumerator<object> TheIterator() {
      //       // the translation of the body of the iterator, with each "yield" turning into a "yield return null;"
      //       yield break;
      //     }
      //   }

      TargetWriter instanceFieldsWriter;
      var w = CreateClass(IdName(iter), iter.TypeArgs, out instanceFieldsWriter, wr);
      // here come the fields
      Constructor ct = null;
      foreach (var member in iter.Members) {
        var f = member as Field;
        if (f != null && !f.IsGhost) {
          DeclareField(IdName(f), false, false, f.Type, f.tok, DefaultValue(f.Type, instanceFieldsWriter, f.tok), instanceFieldsWriter);
        } else if (member is Constructor) {
          Contract.Assert(ct == null);  // we're expecting just one constructor
          ct = (Constructor)member;
        }
      }
      Contract.Assert(ct != null);  // we do expect a constructor
      instanceFieldsWriter.Indent();
      instanceFieldsWriter.WriteLine("this._iter = undefined;");

      // here's the initializer method
      w.Indent(); w.Write("{0}(", IdName(ct));
      string sep = "";
      foreach (var p in ct.Ins) {
        if (!p.IsGhost) {
          // here we rely on the parameters and the corresponding fields having the same names
          w.Write("{0}{1}", sep, IdName(p));
          sep = ", ";
        }
      }
      using (var wBody = w.NewBlock(")")) {
        foreach (var p in ct.Ins) {
          if (!p.IsGhost) {
            wBody.Indent();
            wBody.WriteLine("this.{0} = {0};", IdName(p));
          }
        }
        wBody.Indent(); wBody.WriteLine("this.__iter = this.TheIterator();");
      }
      // here are the enumerator methods
      w.Indent();
      using (var wBody = w.NewBlock("MoveNext()")) {
        wBody.Indent(); wBody.WriteLine("let r = this.__iter.next();");
        wBody.Indent(); wBody.WriteLine("return !r.done;");
      }
      w.Indent();
      var wIter = w.NewBlock("*TheIterator()");
      wIter.Indent(); wIter.WriteLine("let _this = this;");
      return wIter;
    }

    protected override void DeclareDatatype(DatatypeDecl dt, TargetWriter wr) {
      // ===== For inductive datatypes:
      //
      // $module.Dt = class Dt {
      //   constructor(tag) {
      //     this.$tag = tag;
      //   }
      //   static create_Ctor0(field0, field1, ...) {
      //     let $dt = new Dt(0);
      //     $dt.field0 = field0;
      //     $dt.field1 = field1;
      //     ...
      //     return $dt;
      //   }
      //   static create_Ctor1(...) {
      //     let $dt = new Dt(1);
      //     ...
      //   }
      //   ...
      //
      //   get is_Ctor0 { return this.$tag === 0; }
      //   get is_Ctor1 { return this.$tag === 1; }
      //   ...
      //
      //   static get AllSingletonConstructors() {
      //     return this.AllSingletonConstructors_();
      //   }
      //   static *AllSingletonConstructors_() {
      //     yield Berry.create_Ctor0();
      //     ...
      //   }
      //
      //   get dtor_Dtor0() { return this.Dtor0; }
      //   get dtor_Dtor1() { return this.Dtor1; }
      //   ...
      //
      //   toString() {
      //     ...
      //   }
      //   equals(other) {
      //     ...
      //   }
      //   static Rtd(rtd...) {
      //     return class {
      //       static get Default() { return Dt.create_CtorK(...); }
      //     };
      //   }
      // }
      //
      // ===== For co-inductive datatypes:
      //
      // $module.Dt = class Dt {
      //   constructor(tag) {
      //     this.$tag = tag;
      //   }
      //   _D() {
      //     if (this._d === undefined) {
      //       this._d = this._initializer(this);
      //       delete this._initializer;
      //     }
      //     return this._d;
      //   }
      //   static create_Ctor0($dt, field0, field1, ...) {
      //     if ($dt === null) {
      //       $dt = new Dt(0);
      //       $dt._d = $dt;
      //     }
      //     $dt.field0 = field0;
      //     $dt.field1 = field1;
      //     ...
      //     return $dt;
      //   }
      //   static lazy_Ctor0(initializer) {
      //     let dt = new Dt(0);
      //     dt._initializer = initializer;
      //     return dt;
      //   }
      //   static create_Ctor1(initializer) {
      //     let $dt = new Dt(1);
      //     ...
      //   }
      //   ...
      //
      //   get is_Ctor0() { return this.$tag === 0; }
      //   get is_Ctor1() { return this.$tag === 1; }
      //   ...
      //
      //   static get AllSingletonConstructors() {
      //     return this.AllSingletonConstructors_();
      //   }
      //   static *AllSingletonConstructors_() {
      //     yield Berry.create_Ctor0(null);
      //     ...
      //   }
      //
      //   get dtor_Dtor0() { return this._D().Dtor0; }
      //   get dtor_Dtor1() { return this._D().Dtor1; }
      //   ...
      //
      //   toString() {
      //     if ($tag == 0) {
      //       return "module.Dt.Ctor0";
      //     } else if ...
      //   }
      //   equals(other) {
      //     ...
      //   }
      //   static Rtd(rtd...) {
      //     return class {
      //       static get Default() { return Dt.create_CtorK(...); }
      //     };
      //   }
      // }
      if (dt is TupleTypeDecl) {
        // Tuple types are declared once and for all in DafnyRuntime.js
        return;
      }

      string DtT = dt.CompileName;
      string DtT_protected = IdProtect(DtT);

      wr.Indent();
      // from here on, write everything into the new block created here:
      wr = wr.NewNamedBlock("$module.{0} = class {0}", DtT_protected);

      wr.Indent();
      wr.WriteLine("constructor(tag) { this.$tag = tag; }");

      if (dt is CoDatatypeDecl) {
        wr.Indent();
        using (var w0 = wr.NewBlock("_D()")) {
          using (var w1 = EmitIf("this._d === undefined", false, w0)) {
            w1.Indent();
            w1.WriteLine("this._d = this._initializer(this);");
            w1.Indent();
            w1.WriteLine("delete this._initializer;");
          }
          w0.Indent();
          w0.WriteLine("return this._d");
        }
      }

      // query properties
      var i = 0;
      foreach (var ctor in dt.Ctors) {
        // collect the names of non-ghost arguments
        var argNames = new List<string>();
        var k = 0;
        foreach (var formal in ctor.Formals) {
          if (!formal.IsGhost) {
            argNames.Add(FormalName(formal, k));
            k++;
          }
        }
        // datatype:
        //   static create_Ctor0(params) { let $dt = new Dt(tag); $dt.param0 = param0; ...; return $dt; }
        // codatatype:
        //   static create_Ctor0(params) { if ($dt === null) { $dt = new Dt(tag); $dt._d = $dt; } $dt.param0 = param0; ...; return $dt; }
        //   static lazy_Ctor0(initializer) { let dt = new Dt(tag); dt._initializer = initializer; return dt; }
        wr.Indent();
        wr.Write("static create_{0}(", ctor.CompileName);
        if (dt is CoDatatypeDecl) {
          wr.Write("$dt{0}", argNames.Count == 0 ? "" : ",");
        }
        wr.Write(Util.Comma(argNames, nm => nm));
        var w = wr.NewBlock(")");
        if (dt is CoDatatypeDecl) {
          var wThen = EmitIf("$dt === null", false, w);
          wThen.Indent();
          wThen.WriteLine("$dt = new {0}({1});", DtT_protected, i);
          wThen.Indent();
          wThen.WriteLine("$dt._d = $dt;");
        } else {
          w.Indent();
          w.WriteLine("let $dt = new {0}({1});", DtT_protected, i);
        }
        foreach (var arg in argNames) {
          w.Indent();
          w.WriteLine("$dt.{0} = {0};", arg);
        }
        w.Indent();
        w.WriteLine("return $dt;");
        if (dt is CoDatatypeDecl) {
          wr.Indent();
          var wBody = wr.NewNamedBlock("static lazy_{0}(initializer)", ctor.CompileName);
          wBody.Indent();
          wBody.WriteLine("let dt = new {0}({1});", DtT_protected, i);
          wBody.Indent();
          wBody.WriteLine("dt._initializer = initializer;");
          wBody.Indent();
          wBody.WriteLine("return dt;");
        }
        i++;
      }

      // query properties
      i = 0;
      foreach (var ctor in dt.Ctors) {
        // get is_Ctor0() { return _D is Dt_Ctor0; }
        wr.Indent();
        wr.WriteLine("get is_{0}() {{ return this.$tag === {1}; }}", ctor.CompileName, i);
        i++;
      }

      if (dt.HasFinitePossibleValues) {
        Contract.Assert(dt.TypeArgs.Count == 0);
        wr.Indent();
        using (var w = wr.NewNamedBlock("static get AllSingletonConstructors()")) {
          w.Indent();
          w.WriteLine("return this.AllSingletonConstructors_();");
        }
        wr.Indent();
        using (var w = wr.NewNamedBlock("static *AllSingletonConstructors_()")) {
          foreach (var ctor in dt.Ctors) {
            Contract.Assert(ctor.Formals.Count == 0);
            w.Indent();
            w.WriteLine("yield {0}.create_{1}({2});", DtT_protected, ctor.CompileName, dt is CoDatatypeDecl ? "null" : "");
          }
        }
      }
      
      // destructors
      foreach (var ctor in dt.Ctors) {
        foreach (var dtor in ctor.Destructors) {
          if (dtor.EnclosingCtors[0] == ctor) {
            var arg = dtor.CorrespondingFormals[0];
            if (!arg.IsGhost && arg.HasName) {
              // datatype:   get dtor_Dtor0() { return this.Dtor0; }
              // codatatype: get dtor_Dtor0() { return this._D().Dtor0; }
              wr.Indent();
              wr.WriteLine("get dtor_{0}() {{ return this{2}.{1}; }}", arg.CompileName, IdName(arg), dt is CoDatatypeDecl ? "._D()" : "");
            }
          }
        }
      }

      if (dt is CoDatatypeDecl) {
        // toString method
        wr.Indent();
        var w = wr.NewBlock("toString()");
        i = 0;
        foreach (var ctor in dt.Ctors) {
          using (var thn = EmitIf(string.Format("this.$tag === {0}", i), true, w)) {
            thn.Indent();
            var nm = (dt.Module.IsDefaultModule ? "" : dt.Module.Name + ".") + dt.Name + "." + ctor.Name;
            thn.WriteLine("return \"{0}\";", nm);
          }
          i++;
        }
        using (var els = w.NewBlock("")) {
          els.Indent();
          els.WriteLine("return \"{0}.{1}.unexpected\";", dt.Module.CompileName, DtT);
        }

      } else if (dt is IndDatatypeDecl && !(dt is TupleTypeDecl)) {
        // toString method
        wr.Indent();
        using (var w = wr.NewBlock("toString()")) {
          i = 0;
          foreach (var ctor in dt.Ctors) {
            var cw = EmitIf(string.Format("this.$tag === {0}", i), true, w);
            cw.Indent();
            var nm = (dt.Module.IsDefaultModule ? "" : dt.Module.Name + ".") + dt.Name + "." + ctor.Name;
            cw.Write("return \"{0}\"", nm);
            var sep = " + \"(\" + ";
            var anyFormals = false;
            var k = 0;
            foreach (var arg in ctor.Formals) {
              if (!arg.IsGhost) {
                anyFormals = true;
                cw.Write("{0}_dafny.toString(this.{1})", sep, FormalName(arg, k));
                sep = " + \", \" + ";
                k++;
              }
            }
            if (anyFormals) {
              cw.Write(" + \")\"");
            }
            cw.WriteLine(";");
            i++;
          }
          var wElse = w.NewBlock("");
          wElse.Indent();
          wElse.WriteLine("return \"<unexpected>\";");
        }
      }

      // equals method
      wr.Indent();
      using (var w = wr.NewBlock("equals(other)")) {
        using (var thn = EmitIf("this === other", true, w)) {
          EmitReturnExpr("true", thn);
        }
        i = 0;
        foreach (var ctor in dt.Ctors) {
          var thn = EmitIf(string.Format("this.$tag === {0}", i), true, w);
          using (var guard = new TargetWriter(w.IndentLevel)) {
            guard.Write("other.$tag === {0}", i);
            var k = 0;
            foreach (Formal arg in ctor.Formals) {
              if (!arg.IsGhost) {
                string nm = FormalName(arg, k);
                if (IsDirectlyComparable(arg.Type)) {
                  guard.Write(" && this.{0} === other.{0}", nm);
                } else {
                  guard.Write(" && _dafny.areEqual(this.{0}, other.{0})", nm);
                }
                k++;
              }
            }
            EmitReturnExpr(guard.ToString(), thn);
          }
          i++;
        }
        using (var els = w.NewBlock("")) {
          els.Indent();
          els.WriteLine("return false; // unexpected");
        }
      }

      // Note: It is important that the following be a class with a static getter Default(), as opposed
      // to a simple "{ Default: ... }" object, because we need for any recursive calls in the default
      // expression to be evaluated lazily. (More precisely, not evaluated at all, but that will sort
      // itself out due to the restrictions placed by the resolver.)
      //
      // static Rtd(rtd...) {
      //   return class {
      //     static get Default() { return Dt.create_CtorK(...); }
      //   };
      // }
      wr.Indent();
      wr.Write("static Rtd(");
      WriteRuntimeTypeDescriptorsFormals(UsedTypeParameters(dt), true, wr);
      using (var wRtd = wr.NewBlock(")")) {
        wRtd.Indent();
        using (var wClass = wRtd.NewBlock("return class", ";")) {
          wClass.Indent();
          using (var wDefault = wClass.NewBlock("func Rtd_default() Rtd")) {
            wDefault.Indent();
            wDefault.Write("return ");
            DatatypeCtor defaultCtor;
            if (dt is IndDatatypeDecl) {
              defaultCtor = ((IndDatatypeDecl)dt).DefaultCtor;
            } else {
              defaultCtor = ((CoDatatypeDecl)dt).Ctors[0];  // pick any one of them (but pick must be the same as in InitializerIsKnown and HasZeroInitializer)
            }
            var arguments = new TargetWriter();
            string sep = "";
            foreach (var f in defaultCtor.Formals) {
              if (!f.IsGhost) {
                arguments.Write("{0}{1}", sep, DefaultValue(f.Type, wDefault, f.tok));
                sep = ", ";
              }
            }
            EmitDatatypeValue(dt, defaultCtor, dt is CoDatatypeDecl, arguments.ToString(), wDefault);
            wDefault.WriteLine(";");
          }
        }
      }
    }

    protected override void DeclareNewtype(NewtypeDecl nt, TargetWriter wr) {
      TargetWriter instanceFieldsWriter;
      var w = CreateClass(IdName(nt), null, out instanceFieldsWriter, wr);
      if (nt.NativeType != null) {
        w.Indent();
        var wIntegerRangeBody = w.NewBlock("static *IntegerRange(lo, hi)");
        wIntegerRangeBody.Indent();
        var wLoopBody = wIntegerRangeBody.NewBlock("while (lo.isLessThan(hi))");
        wLoopBody.Indent();
        wLoopBody.WriteLine("yield lo.toNumber();");
        EmitIncrementVar("lo", wLoopBody);
      }
      if (nt.WitnessKind == SubsetTypeDecl.WKind.Compiled) { 
        var witness = new TargetWriter(w.IndentLevel);
        if (nt.NativeType == null) {
          TrExpr(nt.Witness, witness, false);
        } else {
          TrParenExpr(nt.Witness, witness, false);
          witness.Write(".toNumber()");
        }
        DeclareField("Witness", true, true, nt.BaseType, nt.tok, witness.ToString(), w);
      }
      w.Indent();
      using (var wDefault = w.NewBlock(String.Format("func {0}_default() {0}", nt.Name))) {
        var udt = new UserDefinedType(nt.tok, nt.Name, nt, new List<Type>());
        var d = TypeInitializationValue(udt, wr, nt.tok, false);
        wDefault.Indent();
        wDefault.WriteLine("return {0};", d);
      }
    }

    protected override void DeclareSubsetType(SubsetTypeDecl sst, TargetWriter wr) {
      TargetWriter instanceFieldsWriter;
      var w = CreateClass(IdName(sst), sst.TypeArgs, out instanceFieldsWriter, wr);
      if (sst.WitnessKind == SubsetTypeDecl.WKind.Compiled) { 
        var witness = new TargetWriter(w.IndentLevel);
        TrExpr(sst.Witness, witness, false);
        DeclareField("Witness", true, true, sst.Rhs, sst.tok, witness.ToString(), w);
      }
      w.Indent();
      using (var wDefault = w.NewBlock(String.Format("func {0}_default() {0}", sst.Name))) {
        var udt = new UserDefinedType(sst.tok, sst.Name, sst, sst.TypeArgs.ConvertAll(tp => (Type)new UserDefinedType(tp)));
        var d = TypeInitializationValue(udt, wr, sst.tok, false);
        wDefault.Indent();
        if (sst.WitnessKind == SubsetTypeDecl.WKind.Compiled) {
          wDefault.WriteLine("return {0}{{ Witness: {1} }}", sst.Name, d);
        } else {
          wDefault.WriteLine("return {0}{{}}", sst.Name);
        }
      }
    }

    protected override void GetNativeInfo(NativeType.Selection sel, out string name, out string literalSuffix, out bool needsCastAfterArithmetic) {
      literalSuffix = "";
      needsCastAfterArithmetic = false;
      switch (sel) {
        case NativeType.Selection.Number:
          name = "number";
          break;
        default:
          Contract.Assert(false);  // unexpected native type
          throw new cce.UnreachableException();  // to please the compiler
      }
    }

    protected override BlockTargetWriter/*?*/ CreateMethod(Method m, bool createBody, TargetWriter wr) {
      if (!createBody) {
        return null;
      }
      
      wr.Indent();
      wr.Write("func ");
      if (!m.IsStatic) {
        wr.Write("(_this * {0}) ", IdName(m.EnclosingClass));
      }
      wr.Write("{0}(", IdName(m));
      var nTypes = WriteRuntimeTypeDescriptorsFormals(m.TypeArgs, false, wr);
      int nIns = WriteFormals(nTypes == 0 ? "" : ", ", m.Ins, wr);
      var w = wr.NewBlock(")");

      if (m.IsTailRecursive) {
        w.Indent();
        w = w.NewBlock("TAIL_CALL_START: for");
        w.Indent();
        w.WriteLine("if false { break TAIL_CALL_START } // suppress unused error");
      }
      var r = new TargetWriter(w.IndentLevel);
      EmitReturn(m.Outs, r);
      w.BodySuffix = r.ToString();
      return w;
    }

    protected override BlockTargetWriter/*?*/ CreateFunction(string name, List<TypeParameter>/*?*/ typeArgs, List<Formal> formals, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, MemberDecl/*?*/ member, TargetWriter wr) {
      if (!createBody) {
        return null;
      }
      wr.Indent();
      wr.Write("func {0}(", name);
      var nTypes = typeArgs == null ? 0 : WriteRuntimeTypeDescriptorsFormals(typeArgs, false, wr);
      int nIns = WriteFormals(nTypes == 0 ? "" : ", ", formals, wr);
      var w = wr.NewBlock(")", ";");
      return w;
    }

    List<TypeParameter> UsedTypeParameters(DatatypeDecl dt) {
      Contract.Requires(dt != null);

      var idt = dt as IndDatatypeDecl;
      if (idt == null) {
        return dt.TypeArgs;
      } else {
        Contract.Assert(idt.TypeArgs.Count == idt.TypeParametersUsedInConstructionByDefaultCtor.Length);
        var tps = new List<TypeParameter>();
        for (int i = 0; i < idt.TypeArgs.Count; i++) {
          if (idt.TypeParametersUsedInConstructionByDefaultCtor[i]) {
            tps.Add(idt.TypeArgs[i]);
          }
        }
        return tps;
      }
    }

    List<Type> UsedTypeParameters(DatatypeDecl dt, List<Type> typeArgs) {
      Contract.Requires(dt != null);
      Contract.Requires(typeArgs != null);
      Contract.Requires(dt.TypeArgs.Count == typeArgs.Count);

      var idt = dt as IndDatatypeDecl;
      if (idt == null) {
        return typeArgs;
      } else {
        Contract.Assert(typeArgs.Count == idt.TypeParametersUsedInConstructionByDefaultCtor.Length);
        var ts = new List<Type>();
        for (int i = 0; i < typeArgs.Count; i++) {
          if (idt.TypeParametersUsedInConstructionByDefaultCtor[i]) {
            ts.Add(typeArgs[i]);
          }
        }
        return ts;
      }
    }

    int WriteRuntimeTypeDescriptorsFields(List<TypeParameter> typeParams, bool useAllTypeArgs, BlockTargetWriter wr) {
      Contract.Requires(typeParams != null);
      Contract.Requires(wr != null);

      int c = 0;
      foreach (var tp in typeParams) {
        if (useAllTypeArgs || tp.Characteristics.MustSupportZeroInitialization) {
          wr.Indent();
          wr.Write("{0} string", "rtd$_" + tp.CompileName);
        }
      }
      return c;
    }

    int WriteRuntimeTypeDescriptorsFormals(List<TypeParameter> typeParams, bool useAllTypeArgs, TargetWriter wr, string prefix = "") {
      Contract.Requires(typeParams != null);
      Contract.Requires(wr != null);

      int c = 0;
      foreach (var tp in typeParams) {
        if (useAllTypeArgs || tp.Characteristics.MustSupportZeroInitialization) {
          wr.Write("{0}{1}", prefix, "rtd$_" + tp.CompileName);
          prefix = ", ";
          c++;
        }
      }
      return c;
    }

    protected override int EmitRuntimeTypeDescriptorsActuals(List<Type> typeArgs, List<TypeParameter> formals, Bpl.IToken tok, bool useAllTypeArgs, TargetWriter wr) {
      var sep = "";
      var c = 0;
      for (int i = 0; i < typeArgs.Count; i++) {
        var actual = typeArgs[i];
        var formal = formals[i];
        if (useAllTypeArgs || formal.Characteristics.MustSupportZeroInitialization) {
          wr.Write("{0}{1}", sep, RuntimeTypeDescriptor(actual, tok, wr));
          sep = ", ";
          c++;
        }
      }
      return c;
    }

    string RuntimeTypeDescriptor(Type type, Bpl.IToken tok, TextWriter wr) {
      Contract.Requires(type != null);
      Contract.Requires(tok != null);
      Contract.Requires(wr != null);

      var xType = type.NormalizeExpandKeepConstraints();
      if (xType is TypeProxy) {
        // unresolved proxy; just treat as bool, since no particular type information is apparently needed for this type
        return "_dafny.Rtd_bool";
      }

      if (xType is BoolType) {
        return "_dafny.Rtd_bool";
      } else if (xType is CharType) {
        return "_dafny.Rtd_char";
      } else if (xType is IntType) {
        return "_dafny.Rtd_int";
      } else if (xType is BigOrdinalType) {
        return "_dafny.BigOrdinal";
      } else if (xType is RealType) {
        return "_dafny.BigRational";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        if (t.NativeType != null) {
          return "_dafny.Rtd_bv_Native";
        } else {
          return "_dafny.Rtd_bv_NonNative";
        }
      } else if (xType is SetType) {
        return "_dafny.Set";
      } else if (xType is MultiSetType) {
        return "_dafny.MultiSet";
      } else if (xType is SeqType) {
        return "_dafny.Seq";
      } else if (xType is MapType) {
        return "_dafny.Map";
      } else if (xType.IsBuiltinArrowType) {
        return "_dafny.Rtd_ref";  // null suffices as a default value, since the function will never be called
      } else if (xType is UserDefinedType) {
        var udt = (UserDefinedType)xType;
        var tp = udt.ResolvedParam;
        if (tp != null) {
          return string.Format("{0}rtd$_{1}", tp.Parent is ClassDecl ? "this." : "", tp.CompileName);
        }
        var cl = udt.ResolvedClass;
        Contract.Assert(cl != null);
        bool isHandle = true;
        if (Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "_dafny.Rtd_ref";
        } else if (cl is ClassDecl) {
          return "_dafny.Rtd_ref";
        } else if (cl is DatatypeDecl) {
          var dt = (DatatypeDecl)cl;
          var w = new TargetWriter();
          w.Write("{0}.Rtd(", dt is TupleTypeDecl ? "_dafny.Tuple" : FullTypeName(udt));
          EmitRuntimeTypeDescriptorsActuals(UsedTypeParameters(dt, udt.TypeArgs), cl.TypeArgs, udt.tok, true, w);
          w.Write(")");
          return w.ToString();
        } else if (xType.IsNonNullRefType) {
          // this initializer shouldn't ever be needed; the compiler is expected to generate an error
          // sooner or later, , but it could be that the the compiler needs to
          // lay down some bits to please the C#'s compiler's different definite-assignment rules.
          return "_dafny.Rtd_ref/*not used*/";
        } else {
          Contract.Assert(cl is NewtypeDecl || cl is SubsetTypeDecl);
          return TypeName_UDT(FullTypeName(udt), udt.TypeArgs, wr, udt.tok);
        }
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected type
      }
    }

    protected override BlockTargetWriter/*?*/ CreateGetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, TargetWriter wr) {
      return CreateFunction("get_" + name, null, null, resultType, tok, isStatic, createBody, null, wr);
    }

    protected override BlockTargetWriter/*?*/ CreateGetterSetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, out TargetWriter setterWriter, TargetWriter wr) {
      if (createBody) {
        var wGet = CreateGetter(name, resultType, tok, isStatic, createBody, wr);

        setterWriter = CreateFunction("set_" + name, null,
          new List<Formal>() { new Formal(tok, "value", resultType, true, false) }, 
          null, tok, isStatic, createBody, null, wr);

        return wGet;
      } else {
        setterWriter = null;
        return null;
      }
    }

    protected override void EmitJumpToTailCallStart(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("continue TAIL_CALL_START;");
    }

    protected override string TypeName(Type type, TextWriter wr, Bpl.IToken tok, MemberDecl/*?*/ member = null) {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<string>() != null);

      var xType = type.NormalizeExpand();
      if (xType is TypeProxy) {
        // unresolved proxy; just treat as ref, since no particular type information is apparently needed for this type
        return "interface {}";
      }

      if (xType is BoolType) {
        return "bool";
      } else if (xType is CharType) {
        return "string";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "*big.Int";
      } else if (xType is RealType) {
        return "*big.Flt";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        return t.NativeType != null ? GetNativeTypeName(t.NativeType) : "BigNumber";
      } else if (xType.AsNewtype != null) {
        NativeType nativeType = xType.AsNewtype.NativeType;
        if (nativeType != null) {
          return GetNativeTypeName(nativeType);
        }
        return TypeName(xType.AsNewtype.BaseType, wr, tok);
      } else if (xType.IsObjectQ) {
        return "interface {}";
      } else if (xType.IsArrayType) {
        ArrayClassDecl at = xType.AsArrayType;
        Contract.Assert(at != null);  // follows from type.IsArrayType
        Type elType = UserDefinedType.ArrayElementType(xType);
        string typeNameSansBrackets, brackets;
        TypeName_SplitArrayName(elType, wr, tok, out typeNameSansBrackets, out brackets);
        return  TypeNameArrayBrackets(at.Dims) + brackets + typeNameSansBrackets;
      } else if (xType is UserDefinedType) {
        var udt = (UserDefinedType)xType;
        var s = FullTypeName(udt, member);
        var cl = udt.ResolvedClass;
        bool isHandle = true;
        if (cl != null && Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "ulong";
        } else if (DafnyOptions.O.IronDafny &&
            !(xType is ArrowType) &&
            cl != null &&
            cl.Module != null &&
            !cl.Module.IsDefaultModule) {
          s = cl.FullCompileName;
        }
        return TypeName_UDT(s, udt.TypeArgs, wr, udt.tok);
      } else if (xType is SetType) {
        Type argType = ((SetType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of set<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnySetClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is SeqType) {
        Type argType = ((SeqType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of seq<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnySeqClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is MultiSetType) {
        Type argType = ((MultiSetType)xType).Arg;
        if (ComplicatedTypeParameterForCompilation(argType)) {
          Error(tok, "compilation of multiset<TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return DafnyMultiSetClass + "<" + TypeName(argType, wr, tok) + ">";
      } else if (xType is MapType) {
        Type domType = ((MapType)xType).Domain;
        Type ranType = ((MapType)xType).Range;
        if (ComplicatedTypeParameterForCompilation(domType) || ComplicatedTypeParameterForCompilation(ranType)) {
          Error(tok, "compilation of map<TRAIT, _> or map<_, TRAIT> is not supported; consider introducing a ghost", wr);
        }
        return "_dafny.Map";
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected type
      }
    }

    public override string TypeInitializationValue(Type type, TextWriter/*?*/ wr, Bpl.IToken/*?*/ tok, bool inAutoInitContext) {
      var xType = type.NormalizeExpandKeepConstraints();
      if (xType is BoolType) {
        return "false";
      } else if (xType is CharType) {
        return "'D'";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "&big.Int {}";
      } else if (xType is RealType) {
        return "_dafny.BigRational.ZERO";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        return t.NativeType != null ? "0" : "&big.Int {}";
      } else if (xType is SetType) {
        return "_dafny.Set.Empty";
      } else if (xType is MultiSetType) {
        return "_dafny.MultiSet.Empty";
      } else if (xType is SeqType) {
        return "_dafny.Seq.of()";
      } else if (xType is MapType) {
        return "_dafny.Map.Empty";
      }

      var udt = (UserDefinedType)xType;
      if (udt.ResolvedParam != null) {
        if (inAutoInitContext && !udt.ResolvedParam.Characteristics.MustSupportZeroInitialization) {
          return "undefined";
        } else {
          return string.Format("{0}.Default", RuntimeTypeDescriptor(udt, udt.tok, wr));
        }
      }
      var cl = udt.ResolvedClass;
      Contract.Assert(cl != null);
      if (cl is NewtypeDecl) {
        var td = (NewtypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(FullTypeName(udt), udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.NativeType != null) {
          return "0";
        } else {
          return TypeInitializationValue(td.BaseType, wr, tok, inAutoInitContext);
        }
      } else if (cl is SubsetTypeDecl) {
        var td = (SubsetTypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(FullTypeName(udt), udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.WitnessKind == SubsetTypeDecl.WKind.Special) {
          // WKind.Special is only used with -->, ->, and non-null types:
          Contract.Assert(ArrowType.IsPartialArrowTypeName(td.Name) || ArrowType.IsTotalArrowTypeName(td.Name) || td is NonNullTypeDecl);
          if (ArrowType.IsPartialArrowTypeName(td.Name)) {
            return "null";
          } else if (ArrowType.IsTotalArrowTypeName(td.Name)) {
            var rangeDefaultValue = TypeInitializationValue(udt.TypeArgs.Last(), wr, tok, inAutoInitContext);
            // return the lambda expression ((Ty0 x0, Ty1 x1, Ty2 x2) => rangeDefaultValue)
            return string.Format("function () {{ return {0}; }}", rangeDefaultValue);
          } else if (((NonNullTypeDecl)td).Class is ArrayClassDecl) {
            // non-null array type; we know how to initialize them
            var arrayClass = (ArrayClassDecl)((NonNullTypeDecl)td).Class;
            if (arrayClass.Dims == 1) {
              return "[]";
            } else {
              return string.Format("_dafny.newArray(undefined, {0})", Util.Comma(arrayClass.Dims, _ => "0"));
            }
          } else {
            // non-null (non-array) type
            // even though the type doesn't necessarily have a known initializer, it could be that the the compiler needs to
            // lay down some bits to please the C#'s compiler's different definite-assignment rules.
            return "null";
          }
        } else {
          return TypeInitializationValue(td.RhsWithArgument(udt.TypeArgs), wr, tok, inAutoInitContext);
        }
      } else if (cl is ClassDecl) {
        bool isHandle = true;
        if (Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "0";
        } else {
          return "null";
        }
      } else if (cl is DatatypeDecl) {
        var dt = (DatatypeDecl)cl;
        var s = dt is TupleTypeDecl ? "_dafny.Tuple" : FullTypeName(udt);
        var w = new TargetWriter();
        w.Write("{0}.Rtd(", s);
        EmitRuntimeTypeDescriptorsActuals(UsedTypeParameters(dt, udt.TypeArgs), dt.TypeArgs, udt.tok, true, w);
        w.Write(").Default");
        return w.ToString();
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected type
      }

    }

    protected override string TypeName_UDT(string fullCompileName, List<Type> typeArgs, TextWriter wr, Bpl.IToken tok) {
      Contract.Requires(fullCompileName != null);
      Contract.Requires(typeArgs != null);
      string s = IdProtect(fullCompileName);
      return s;
    }

    protected override string TypeName_Companion(Type type, TextWriter wr, Bpl.IToken tok, MemberDecl/*?*/ member) {
      // There are no companion classes for JavaScript
      return TypeName(type, wr, tok, member);
    }

    // ----- Declarations -------------------------------------------------------------

    protected override void DeclareField(string name, bool isStatic, bool isConst, Type type, Bpl.IToken tok, string rhs, TargetWriter wr) {
      wr.Indent();
      if (isStatic) {
        var w = wr.NewNamedBlock("static get {0}()", name);
        EmitReturnExpr(rhs, w);
      } else {
        wr.WriteLine("this.{0} = {1};", name, rhs);
      }
    }

    protected override bool DeclareFormal(string prefix, string name, Type type, Bpl.IToken tok, bool isInParam, TextWriter wr) {
      if (isInParam) {
        wr.Write("{0}{1}", prefix, name);
        return true;
      } else {
        return false;
      }
    }

    protected override void DeclareLocalVar(string name, Type/*?*/ type, Bpl.IToken/*?*/ tok, bool leaveRoomForRhs, string/*?*/ rhs, TargetWriter wr) {
      wr.Indent();
      wr.Write("var {0}{1}", name, type != null ? " " + TypeName(type, wr, tok) : "");
      
      if (leaveRoomForRhs) {
        Contract.Assert(rhs == null);  // follows from precondition
      } else if (rhs != null) {
        wr.WriteLine(" = {0}", rhs);
      } else {
        wr.WriteLine("");
      }
    }

    protected override TargetWriter DeclareLocalVar(string name, Type/*?*/ type, Bpl.IToken/*?*/ tok, TargetWriter wr) {
      wr.Indent();
      wr.Write("var {0}{1} = ", name, type != null ? " " + TypeName(type, wr, tok) : "");
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.WriteLine("");
      return w;
    }

    protected override bool UseReturnStyleOuts(Method m, int nonGhostOutCount) => true;

    protected override void DeclareOutCollector(string collectorVarName, TargetWriter wr) {
      wr.Write("let {0} = ", collectorVarName);
    }

    protected override void DeclareLocalOutVar(string name, Type type, Bpl.IToken tok, string rhs, TargetWriter wr) {
      DeclareLocalVar(name, type, tok, false, rhs, wr);
    }

    protected override void EmitOutParameterSplits(string outCollector, List<string> actualOutParamNames, TargetWriter wr) {
      if (actualOutParamNames.Count == 1) {
        EmitAssignment(actualOutParamNames[0], outCollector, wr);
      } else {
        for (var i = 0; i < actualOutParamNames.Count; i++) {
          wr.Indent();
          wr.WriteLine("{0} = {1}[{2}];", actualOutParamNames[i], outCollector, i);
        }
      }
    }

    protected override void EmitActualTypeArgs(List<Type> typeArgs, Bpl.IToken tok, TextWriter wr) {
      // emit nothing
    }

    protected override string GenerateLhsDecl(string target, Type/*?*/ type, TextWriter wr, Bpl.IToken tok) {
      return "let " + target;
    }

    // ----- Statements -------------------------------------------------------------

    protected override void EmitPrintStmt(TargetWriter wr, Expression arg) {
      wr.Indent();
      wr.Write("printAny(");
      TrExpr(arg, wr, false);
      wr.WriteLine(")");
    }

    protected override void EmitReturn(List<Formal> outParams, TargetWriter wr) {
      outParams = outParams.Where(f => !f.IsGhost).ToList();
      wr.Indent();
      if (outParams.Count == 0) {
        wr.WriteLine("return");
      } else if (outParams.Count == 1) {
        wr.WriteLine("return {0}", IdName(outParams[0]));
      } else {
        wr.WriteLine("return [{0}]", Util.Comma(outParams, IdName));
      }
    }

    protected override TargetWriter CreateLabeledCode(string label, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("L{0}:", label);
    }

    protected override void EmitBreak(string/*?*/ label, TargetWriter wr) {
      wr.Indent();
      if (label == null) {
        wr.WriteLine("break;");
      } else {
        wr.WriteLine("break L{0};", label);
      }
    }

    protected override void EmitYield(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("yield null;");
    }

    protected override void EmitAbsurd(string/*?*/ message, TargetWriter wr) {
      if (message == null) {
        message = "unexpected control point";
      }
      wr.Indent();
      wr.WriteLine("throw new Error(\"{0}\");", message);
    }

    protected override BlockTargetWriter CreateForLoop(string indexVar, string bound, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("for (let {0} = 0; {0} < {1}; {0}++)", indexVar, bound);
    }

    protected override BlockTargetWriter CreateDoublingForLoop(string indexVar, int start, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("for (let {0} = new BigNumber({1}); ; {0} = {0}.multipliedBy(2))", indexVar, start);
    }

    protected override void EmitIncrementVar(string varName, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("{0} = {0}.plus(1);", varName);
    }

    protected override void EmitDecrementVar(string varName, TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("{0} = {0}.minus(1);", varName);
    }

    protected override string GetQuantifierName(string bvType) {
      return string.Format("_dafny.Quantifier");
    }

    protected override BlockTargetWriter CreateForeachLoop(string boundVar, out TargetWriter collectionWriter, TargetWriter wr, string/*?*/ altBoundVarName = null, Type/*?*/ altVarType = null, Bpl.IToken/*?*/ tok = null) {
      wr.Indent();
      wr.Write("for (const {0} of ", boundVar);
      collectionWriter = new TargetWriter(wr.IndentLevel);
      wr.Append(collectionWriter);
      if (altBoundVarName == null) {
        return wr.NewBlock(")");
      } else if (altVarType == null) {
        return wr.NewBlockWithPrefix(")", "{0} = {1};", altBoundVarName, boundVar);
      } else {
        return wr.NewBlockWithPrefix(")", "let {0} = {1};", altBoundVarName, boundVar);
      }
    }

    // ----- Expressions -------------------------------------------------------------

    protected override void EmitNew(Type type, Bpl.IToken tok, CallStmt/*?*/ initCall, TargetWriter wr) {
      var cl = (type.NormalizeExpand() as UserDefinedType)?.ResolvedClass;
      if (cl != null && cl.Name == "object") {
        wr.Write("_dafny.NewObject()");
      } else {
        wr.Write("new {0}(", TypeName(type, wr, tok));
        EmitRuntimeTypeDescriptorsActuals(type.TypeArgs, cl.TypeArgs, tok, false, wr);
        wr.Write(")");
      }
    }

    protected override void EmitNewArray(Type elmtType, Bpl.IToken tok, List<Expression> dimensions, bool mustInitialize, TargetWriter wr) {
      var initValue = mustInitialize ? DefaultValue(elmtType, wr, tok) : null;
      if (dimensions.Count == 1) {
        // handle the common case of 1-dimensional arrays separately
        wr.Write("Array(");
        TrParenExpr(dimensions[0], wr, false);
        wr.Write(".toNumber())");
        if (initValue != null) {
          wr.Write(".fill({0})", initValue);
        }
      } else {
        // the general case
        wr.Write("_dafny.newArray({0}", initValue ?? "undefined");
        foreach (var dim in dimensions) {
          wr.Write(", ");
          TrParenExpr(dim, wr, false);
          wr.Write(".toNumber()");
        }
        wr.Write(")");
      }
    }

    protected override void EmitLiteralExpr(TextWriter wr, LiteralExpr e) {
      if (e is StaticReceiverExpr) {
        wr.Write(TypeName(e.Type, wr, e.tok));
      } else if (e.Value == null) {
        wr.Write("null");
      } else if (e.Value is bool) {
        wr.Write((bool)e.Value ? "true" : "false");
      } else if (e is CharLiteralExpr) {
        var v = (string)e.Value;
        wr.Write("'{0}'", v == "\\0" ? "\\u0000" : v);  // JavaScript doesn't have a \0
      } else if (e is StringLiteralExpr) {
        var str = (StringLiteralExpr)e;
        // TODO: the string should be converted to a Dafny seq<char>
        TrStringLiteral(str, wr);
      } else if (AsNativeType(e.Type) != null) {
        wr.Write((BigInteger)e.Value);
      } else if (e.Value is BigInteger) {
        var i = (BigInteger)e.Value;
        EmitIntegerLiteral(i, wr);
      } else if (e.Value is Basetypes.BigDec) {
        var n = (Basetypes.BigDec)e.Value;
        if (0 <= n.Exponent) {
          wr.Write("new _dafny.BigRational(new BigNumber(\"{0}", n.Mantissa);
          for (int i = 0; i < n.Exponent; i++) {
            wr.Write("0");
          }
          wr.Write("\"))");
        } else {
          wr.Write("new _dafny.BigRational(");
          EmitIntegerLiteral(n.Mantissa, wr);
          wr.Write(", new BigNumber(\"1");
          for (int i = n.Exponent; i < 0; i++) {
            wr.Write("0");
          }
          wr.Write("\"))");
        }
      } else {
        Contract.Assert(false); throw new cce.UnreachableException();  // unexpected literal
      }
    }
    void EmitIntegerLiteral(BigInteger i, TextWriter wr) {
      Contract.Requires(wr != null);
      if (long.MinValue <= i && i <= long.MaxValue) {
        wr.Write("big.NewInt({0})", i);
      } else {
        wr.Write("new(big.Int).SetString(\"{0}\")", i);
      }
    }

    protected override void EmitStringLiteral(string str, bool isVerbatim, TextWriter wr) {
      var n = str.Length;
      if (!isVerbatim) {
        wr.Write("\"{0}\"", str);
      } else {
        wr.Write("\"");
        for (var i = 0; i < n; i++) {
          if (str[i] == '\"' && i+1 < n && str[i+1] == '\"') {
            wr.Write("\\\"");
            i++;
          } else if (str[i] == '\\') {
            wr.Write("\\\\");
          } else if (str[i] == '\n') {
            wr.Write("\\n");
          } else if (str[i] == '\r') {
            wr.Write("\\r");
          } else {
            wr.Write(str[i]);
          }
        }
        wr.Write("\"");
      }
    }

    protected override TargetWriter EmitBitvectorTruncation(BitvectorType bvType, bool surroundByUnchecked, TargetWriter wr) {
      string nativeName = null, literalSuffix = null;
      bool needsCastAfterArithmetic = false;
      if (bvType.NativeType != null) {
        GetNativeInfo(bvType.NativeType.Sel, out nativeName, out literalSuffix, out needsCastAfterArithmetic);
      }

      if (bvType.NativeType == null) {
        wr.Write("(");
        var middle = new TargetWriter(wr.IndentLevel);
        wr.Append(middle);
        wr.Write(").mod(new BigNumber(2).exponentiatedBy({0}))", bvType.Width);
        return middle;
      } else if (bvType.NativeType.Bitwidth != bvType.Width) {
        // no truncation needed
        return wr;
      } else {
        wr.Write("((");
        var middle = new TargetWriter(wr.IndentLevel);
        wr.Append(middle);
        // print in hex, because that looks nice
        wr.Write(") & 0x{0:X}{1})", (1UL << bvType.Width) - 1, literalSuffix);
        return middle;
      }
    }

    protected override void EmitRotate(Expression e0, Expression e1, bool isRotateLeft, TargetWriter wr, bool inLetExprBody, FCE_Arg_Translator tr) {
      string nativeName = null, literalSuffix = null;
      bool needsCast = false;
      var nativeType = AsNativeType(e0.Type);
      if (nativeType != null) {
        GetNativeInfo(nativeType.Sel, out nativeName, out literalSuffix, out needsCast);
      }

      var bv = e0.Type.AsBitVectorType;
      if (bv.Width == 0) {
        tr(e0, wr, inLetExprBody);
      } else {
        wr.Write("_dafny.{0}(", isRotateLeft ? "RotateLeft" : "RotateRight");
        tr(e0, wr, inLetExprBody);
        wr.Write(", (");
        tr(e1, wr, inLetExprBody);
        wr.Write(").toNumber(), {0})", bv.Width);
        if (needsCast) {
          wr.Write(".toNumber()");
        }
      }
    }

    protected override void EmitEmptyTupleList(string tupleTypeArgs, TargetWriter wr) {
      wr.Write("[]", tupleTypeArgs);
    }

    protected override TargetWriter EmitAddTupleToList(string ingredients, string tupleTypeArgs, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0}.push(_dafny.Tuple.of(", ingredients, tupleTypeArgs);
      var wrTuple = new TargetWriter(wr.IndentLevel);
      wr.Append(wrTuple);
      wr.WriteLine("));");
      return wrTuple;
    }

    protected override void EmitTupleSelect(string prefix, int i, TargetWriter wr) {
      wr.Write("{0}[{1}]", prefix, i);
    }

    protected override string IdProtect(string name) {
      return PublicIdProtect(name);
    }
    public static string PublicIdProtect(string name) {
      Contract.Requires(name != null);
      switch (name) {
        case "arguments":
        case "await":
        case "boolean":
        case "byte":
        case "catch":
        case "continue":
        case "debugger":
        case "default":
        case "delete":
        case "do":
        case "double":
        case "enum":
        case "eval":
        case "final":
        case "finally":
        case "float":
        case "for":
        case "goto":
        case "implements":
        case "instanceof":
        case "interface":
        case "let":
        case "long":
        case "native":
        case "package":
        case "private":
        case "protected":
        case "public":
        case "short":
        case "super":
        case "switch":
        case "synchronized":
        case "throw":
        case "throws":
        case "transient":
        case "try":
        case "typeof":
        case "void":
        case "volatile":
        case "with":
          return "_$$_" + name;
        default:
          return name;
      }
    }

    protected override string FullTypeName(UserDefinedType udt, MemberDecl/*?*/ member = null) {
      Contract.Requires(udt != null);
      if (udt is ArrowType) {
        return ArrowType.Arrow_FullCompileName;
      }
      var cl = udt.ResolvedClass;
      if (cl == null) {
        return IdProtect(udt.CompileName);
      } else if (cl is ClassDecl cdecl && cdecl.IsDefaultClass && Attributes.Contains(cl.Module.Attributes, "extern") &&
        member != null && Attributes.Contains(member.Attributes, "extern")) {
        // omit the default class name ("_default") in extern modules, when the class is used to qualify an extern member
        Contract.Assert(!cl.Module.IsDefaultModule);  // default module is not marked ":extern"
        return IdProtect(cl.Module.CompileName);
      } else {
        return IdProtect(cl.Module.CompileName) + "." + IdProtect(cl.CompileName);
      }
    }

    protected override void EmitThis(TargetWriter wr) {
      wr.Write("_this");
    }

    protected override void EmitDatatypeValue(DatatypeValue dtv, string arguments, TargetWriter wr) {
      var dt = dtv.Ctor.EnclosingDatatype;
      EmitDatatypeValue(dt, dtv.Ctor, dtv.IsCoCall, arguments, wr);
    }

    void EmitDatatypeValue(DatatypeDecl dt, DatatypeCtor ctor, bool isCoCall, string arguments, TargetWriter wr) {
      var dtName = dt.FullCompileName;
      var ctorName = ctor.CompileName;

      if (dt is TupleTypeDecl) {
        wr.Write("_dafny.Tuple.of({0})", arguments);
      } else if (!isCoCall) {
        // Ordinary constructor (that is, one that does not guard any co-recursive calls)
        // Generate:  Dt.create_Ctor(arguments)
        wr.Write("{0}.create_{1}({2}{3})",
          dtName, ctorName,
          dt is IndDatatypeDecl ? "" : arguments.Length == 0 ? "null" : "null, ",
          arguments);
      } else {
        // Co-recursive call
        // Generate:  Dt.lazy_Ctor(($dt) => Dt.create_Ctor($dt, args))
        wr.Write("{0}.lazy_{1}(($dt) => ", dtName, ctorName);
        wr.Write("{0}.create_{1}($dt{2}{3})", dtName, ctorName, arguments.Length == 0 ? "" : ", ", arguments);
        wr.Write(")");
      }
    }

    protected override void GetSpecialFieldInfo(SpecialField.ID id, object idParam, out string compiledName, out string preString, out string postString) {
      compiledName = "";
      preString = "";
      postString = "";
      switch (id) {
        case SpecialField.ID.UseIdParam:
          compiledName = (string)idParam;
          break;
        case SpecialField.ID.ArrayLength:
        case SpecialField.ID.ArrayLengthInt:
          if (idParam == null) {
            compiledName = "length";
          } else {
            compiledName = "dims[" + (int)idParam + "]";
          }
          if (id == SpecialField.ID.ArrayLength) {
            preString = "new BigNumber(";
            postString = ")";
          }
          break;
        case SpecialField.ID.Floor:
          compiledName = "toBigNumber()";
          break;
        case SpecialField.ID.IsLimit:
          preString = "_dafny.BigOrdinal.IsLimit(";
          postString = ")";
          break;
        case SpecialField.ID.IsSucc:
          preString = "_dafny.BigOrdinal.IsSucc(";
          postString = ")";
          break;
        case SpecialField.ID.Offset:
          preString = "_dafny.BigOrdinal.Offset(";
          postString = ")";
          break;
        case SpecialField.ID.IsNat:
          preString = "_dafny.BigOrdinal.IsNat(";
          postString = ")";
          break;
        case SpecialField.ID.Keys:
          compiledName = "Keys";
          break;
        case SpecialField.ID.Values:
          compiledName = "Values";
          break;
        case SpecialField.ID.Items:
          compiledName = "Items";
          break;
        case SpecialField.ID.Reads:
          compiledName = "_reads";
          break;
        case SpecialField.ID.Modifies:
          compiledName = "_modifies";
          break;
        case SpecialField.ID.New:
          compiledName = "_new";
          break;
        default:
          Contract.Assert(false); // unexpected ID
          break;
      }
    }

    protected override void EmitMemberSelect(MemberDecl member, bool isLValue, TargetWriter wr) {
      if (isLValue && member is ConstantField) {
        wr.Write("._{0}", member.CompileName);
      } else if (member is DatatypeDestructor dtor && dtor.EnclosingClass is TupleTypeDecl) {
        wr.Write("[{0}]", dtor.Name);
      } else if (!isLValue && member is SpecialField sf) {
        string compiledName, preStr, postStr;
        GetSpecialFieldInfo(sf.SpecialId, sf.IdParam, out compiledName, out preStr, out postStr);
        if (compiledName.Length != 0) {
          wr.Write(".{0}", compiledName);
        } else {
          // this member selection is handled by some kind of enclosing function call, so nothing to do here
        }
      } else {
        wr.Write(".{0}", IdName(member));
      }
    }

    protected override void EmitArraySelect(List<string> indices, TargetWriter wr) {
      if (indices.Count == 1) {
        wr.Write("[{0}]", indices[0]);
      } else {
        wr.Write(".elmts");
        foreach (var index in indices) {
          wr.Write("[{0}]", index);
        }
      }
    }

    protected override void EmitArraySelect(List<Expression> indices, bool inLetExprBody, TargetWriter wr) {
      Contract.Assert(indices != null && 1 <= indices.Count);  // follows from precondition
      if (indices.Count == 1) {
        wr.Write("[");
        TrExpr(indices[0], wr, inLetExprBody);
        wr.Write("]");
      } else {
        wr.Write(".elmts");
        foreach (var index in indices) {
          wr.Write("[");
          TrExpr(index, wr, inLetExprBody);
          wr.Write("]");
        }
      }
    }

    protected override string ArrayIndexToInt(string arrayIndex) {
      return string.Format("new BigNumber({0})", arrayIndex);
    }

    protected override void EmitExprAsInt(Expression expr, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(expr, wr, inLetExprBody);
      if (AsNativeType(expr.Type) == null) {
        wr.Write(".toNumber()");
      }
    }

    protected override void EmitIndexCollectionSelect(Expression source, Expression index, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(source, wr, inLetExprBody);
      if (source.Type.NormalizeExpand() is SeqType) {
        // seq
        wr.Write("[");
        TrExpr(index, wr, inLetExprBody);
        wr.Write("]");
      } else {
        // map or imap
        wr.Write(".get(");
        TrExpr(index, wr, inLetExprBody);
        wr.Write(")");
      }
    }

    protected override void EmitIndexCollectionUpdate(Expression source, Expression index, Expression value, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(source, wr, inLetExprBody);
      wr.Write(".update(");
      TrExpr(index, wr, inLetExprBody);
      wr.Write(", ");
      TrExpr(value, wr, inLetExprBody);
      wr.Write(")");
    }

    protected override void EmitSeqSelectRange(Expression source, Expression/*?*/ lo, Expression/*?*/ hi, bool fromArray, bool inLetExprBody, TargetWriter wr) {
      if (fromArray) {
        wr.Write("_dafny.Seq.of(...");
      }
      TrParenExpr(source, wr, inLetExprBody);
      if (lo != null) {
        wr.Write(".slice(");
        TrExpr(lo, wr, inLetExprBody);
        if (hi != null) {
          wr.Write(", ");
          TrExpr(hi, wr, inLetExprBody);
        }
        wr.Write(")");
      } else if (hi != null) {
        wr.Write(".slice(0, ");
        TrExpr(hi, wr, inLetExprBody);
        wr.Write(")");
      } else if (fromArray) {
        wr.Write(".slice()");
      }
      if (fromArray) {
        wr.Write(")");
      }
    }

    protected override void EmitMultiSetFormingExpr(MultiSetFormingExpr expr, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr("_dafny.MultiSet.FromArray", expr.E, wr, inLetExprBody);
    }

    protected override void EmitApplyExpr(Type functionType, Bpl.IToken tok, Expression function, List<Expression> arguments, bool inLetExprBody, TargetWriter wr) {
      TrParenExpr(function, wr, inLetExprBody);
      TrExprList(arguments, wr, inLetExprBody);
    }

    protected override TargetWriter EmitBetaRedex(string boundVars, List<Expression> arguments, string typeArgs, bool inLetExprBody, TargetWriter wr) {
      wr.Write("(({0}) => ", boundVars);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.Write(")");
      TrExprList(arguments, wr, inLetExprBody);
      return w;
    }

    protected override void EmitDestructor(string source, Formal dtor, int formalNonGhostIndex, DatatypeCtor ctor, List<Type> typeArgs, TargetWriter wr) {
      if (ctor.EnclosingDatatype is TupleTypeDecl) {
        wr.Write("({0})[{1}]", source, formalNonGhostIndex);
      } else {
        var dtorName = FormalName(dtor, formalNonGhostIndex);
        wr.Write("({0}){1}.{2}", source, ctor.EnclosingDatatype is CoDatatypeDecl ? "._D()" : "", dtorName);
      }
    }

    protected override BlockTargetWriter CreateLambda(List<Type> inTypes, Bpl.IToken tok, List<string> inNames, Type resultType, TargetWriter wr) {
      wr.Write("function (");
      Contract.Assert(inTypes.Count == inNames.Count);  // guaranteed by precondition
      for (var i = 0; i < inNames.Count; i++) {
        wr.Write("{0}{1}", i == 0 ? "" : ", ", inNames[i]);
      }
      var w = wr.NewBlock(")");
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      return w;
    }

    protected override TargetWriter CreateIIFE_ExprBody(Expression source, bool inLetExprBody, Type sourceType, Bpl.IToken sourceTok, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      var w = wr.NewNamedBlock("function ({0})", bvName);
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      w.Indent();
      w.Write("return ");
      w.BodySuffix = ";" + w.NewLine;
      TrParenExpr(source, wr, inLetExprBody);
      return w;
    }

    protected override TargetWriter CreateIIFE_ExprBody(string source, Type sourceType, Bpl.IToken sourceTok, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      var w = wr.NewNamedBlock("function ({0})", bvName);
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      w.Indent();
      w.Write("return ");
      w.BodySuffix = ";" + w.NewLine;
      wr.Write("({0})", source);
      return w;
    }

    protected override BlockTargetWriter CreateIIFE0(Type resultType, Bpl.IToken resultTok, TargetWriter wr) {
      var w = wr.NewBlock("function ()", "()");
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      return w;
    }

    protected override BlockTargetWriter CreateIIFE1(int source, Type resultType, Bpl.IToken resultTok, string bvName, TargetWriter wr) {
      var w = wr.NewNamedBlock("function ({0})", bvName);
      w.SetBraceStyle(BlockTargetWriter.BraceStyle.Space, BlockTargetWriter.BraceStyle.Nothing);
      wr.Write("({0})", source);
      return w;
    }

    protected override void EmitUnaryExpr(ResolvedUnaryOp op, Expression expr, bool inLetExprBody, TargetWriter wr) {
      switch (op) {
        case ResolvedUnaryOp.BoolNot:
          TrParenExpr("!", expr, wr, inLetExprBody);
          break;
        case ResolvedUnaryOp.BitwiseNot:
          if (AsNativeType(expr.Type) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(expr.Type.AsBitVectorType.Width == 0);
            wr.Write("0");
          } else {
            wr.Write("_dafny.BitwiseNot(");
            TrExpr(expr, wr, inLetExprBody);
            wr.Write(", {0})", expr.Type.AsBitVectorType.Width);
          }
          break;
        case ResolvedUnaryOp.Cardinality:
          TrParenExpr("new BigNumber(", expr, wr, inLetExprBody);
          if (expr.Type.AsMultiSetType != null) {
            wr.Write(".cardinality())");
          } else {
            wr.Write(".length)");
          }
          break;
        default:
          Contract.Assert(false); throw new cce.UnreachableException();  // unexpected unary expression
      }
    }

    bool IsDirectlyComparable(Type t) {
      Contract.Requires(t != null);
      return t.IsBoolType || t.IsCharType || AsNativeType(t) != null || t.IsRefType;
    }

    protected override void CompileBinOp(BinaryExpr.ResolvedOpcode op,
      Expression e0, Expression e1, Bpl.IToken tok, Type resultType,
      out string opString,
      out string preOpString,
      out string postOpString,
      out string callString,
      out string staticCallString,
      out bool reverseArguments,
      out bool truncateResult,
      out bool convertE1_to_int,
      TextWriter errorWr) {

      opString = null;
      preOpString = "";
      postOpString = "";
      callString = null;
      staticCallString = null;
      reverseArguments = false;
      truncateResult = false;
      convertE1_to_int = false;

      switch (op) {
        case BinaryExpr.ResolvedOpcode.Iff:
          opString = "==="; break;
        case BinaryExpr.ResolvedOpcode.Imp:
          preOpString = "!"; opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.Or:
          opString = "||"; break;
        case BinaryExpr.ResolvedOpcode.And:
          opString = "&&"; break;
        case BinaryExpr.ResolvedOpcode.BitwiseAnd:
          if (AsNativeType(resultType) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(resultType.AsBitVectorType.Width < 32);
            opString = "&";
          } else {
            staticCallString = "_dafny.BitwiseAnd";
          }
          break;
        case BinaryExpr.ResolvedOpcode.BitwiseOr:
          if (AsNativeType(resultType) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(resultType.AsBitVectorType.Width < 32);
            opString = "|";
          } else {
            staticCallString = "_dafny.BitwiseOr";
          }
          break;
        case BinaryExpr.ResolvedOpcode.BitwiseXor:
          if (AsNativeType(resultType) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(resultType.AsBitVectorType.Width < 32);
            opString = "^";
          } else {
            staticCallString = "_dafny.BitwiseXor";
          }
          break;

        case BinaryExpr.ResolvedOpcode.EqCommon: {
            if (IsHandleComparison(tok, e0, e1, errorWr)) {
              opString = "===";
            } else if (IsDirectlyComparable(e0.Type)) {
              opString = "===";
            } else if (e0.Type.IsIntegerType || e0.Type.IsBitVectorType) {
              callString = "isEqualTo";
            } else if (e0.Type.IsRealType) {
              callString = "equals";
            } else {
              staticCallString = "_dafny.areEqual";
            }
            break;
          }
        case BinaryExpr.ResolvedOpcode.NeqCommon: {
            if (IsHandleComparison(tok, e0, e1, errorWr)) {
              opString = "!==";
            } else if (IsDirectlyComparable(e0.Type)) {
              opString = "!==";
            } else if (e0.Type.IsIntegerType) {
              preOpString = "!";
              callString = "isEqualTo";
            } else if (e0.Type.IsRealType) {
              preOpString = "!";
              callString = "equals";
            } else {
              preOpString = "!";
              staticCallString = "_dafny.areEqual";
            }
            break;
          }

        case BinaryExpr.ResolvedOpcode.Lt:
        case BinaryExpr.ResolvedOpcode.LtChar:
          if (e0.Type.IsIntegerType || e0.Type.IsRealType) {
            callString = "isLessThan";
          } else {
            opString = "<";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Le:
        case BinaryExpr.ResolvedOpcode.LeChar:
          if (e0.Type.IsIntegerType) {
            callString = "isLessThanOrEqualTo";
          } else if (e0.Type.IsRealType) {
            callString = "isAtMost";
          } else {
            opString = "<=";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Ge:
        case BinaryExpr.ResolvedOpcode.GeChar:
          if (e0.Type.IsIntegerType) {
            callString = "isLessThanOrEqualTo";
            reverseArguments = true;
          } else if (e0.Type.IsRealType) {
            callString = "isAtMost";
            reverseArguments = true;
          } else {
            opString = ">=";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Gt:
        case BinaryExpr.ResolvedOpcode.GtChar:
          if (e0.Type.IsIntegerType || e0.Type.IsRealType) {
            callString = "isLessThan";
            reverseArguments = true;
          } else {
            opString = ">";
          }
          break;
        case BinaryExpr.ResolvedOpcode.LeftShift:
          if (AsNativeType(resultType) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(resultType.AsBitVectorType.Width == 0);
            opString = "+";  // 0 + 0 == 0 == 0 << 0
             convertE1_to_int = true;
          } else {
            staticCallString = "_dafny.ShiftLeft";
            truncateResult = true; convertE1_to_int = true;
          }
          break;
        case BinaryExpr.ResolvedOpcode.RightShift:
          if (AsNativeType(resultType) != null) {
            // JavaScript bitwise operators are weird (numeric operands are first converted into
            // signed 32-bit values), and it could be easy to forget how weird they are.
            // Therefore, as a protective measure, the following assert is here to catch against any future
            // change that would render this translation incorrect.
            Contract.Assert(resultType.AsBitVectorType.Width == 0);
            opString = "+";  // 0 + 0 == 0 == 0 << 0
             convertE1_to_int = true;
          } else {
            staticCallString = "_dafny.ShiftRight";
            truncateResult = true; convertE1_to_int = true;
          }
          break;
        case BinaryExpr.ResolvedOpcode.Add:
          if (resultType.IsIntegerType || resultType.IsRealType || resultType.IsBigOrdinalType) {
            callString = "plus"; truncateResult = true;
          } else if (AsNativeType(resultType) != null) {
            opString = "+";
          } else {
            callString = "plus"; truncateResult = true;
          }
          break;
        case BinaryExpr.ResolvedOpcode.Sub:
          if (resultType.IsIntegerType || resultType.IsRealType || resultType.IsBigOrdinalType) {
            callString = "minus"; truncateResult = true;
          } else if (AsNativeType(resultType) != null) {
            opString = "-";
          } else {
            callString = "minus"; truncateResult = true;
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mul:
          if (resultType.IsIntegerType || resultType.IsRealType) {
            callString = "multipliedBy"; truncateResult = true;
          } else if (AsNativeType(resultType) != null) {
            opString = "*";
          } else {
            callString = "multipliedBy"; truncateResult = true;
          }
          break;
        case BinaryExpr.ResolvedOpcode.Div:
          if (resultType.IsIntegerType) {
            staticCallString = "_dafny.EuclideanDivision";
          } else if (resultType.IsRealType) {
            callString = "dividedBy";
          } else if (AsNativeType(resultType) == null) {
            callString = "dividedToIntegerBy";
          } else if (AsNativeType(resultType).LowerBound < BigInteger.Zero) {
            staticCallString = "_dafny.EuclideanDivisionNumber";
          } else {
            opString = "/";
          }
          break;
        case BinaryExpr.ResolvedOpcode.Mod:
          if (resultType.IsIntegerType) {
            callString = "mod";
          } else if (AsNativeType(resultType) == null) {
            callString = "mod";
          } else if (AsNativeType(resultType).LowerBound < BigInteger.Zero) {
            staticCallString = "_dafny.EuclideanModuloNumber";
          } else {
            opString = "%";
          }
          break;
        case BinaryExpr.ResolvedOpcode.SetEq:
        case BinaryExpr.ResolvedOpcode.MultiSetEq:
        case BinaryExpr.ResolvedOpcode.MapEq:
          callString = "equals"; break;
        case BinaryExpr.ResolvedOpcode.SeqEq:
          // a sequence may be represented as an array or as a string
          staticCallString = "_dafny.areEqual"; break;
        case BinaryExpr.ResolvedOpcode.SetNeq:
        case BinaryExpr.ResolvedOpcode.MultiSetNeq:
        case BinaryExpr.ResolvedOpcode.MapNeq:
          preOpString = "!"; callString = "equals"; break;
        case BinaryExpr.ResolvedOpcode.SeqNeq:
          // a sequence may be represented as an array or as a string
          preOpString = "!"; staticCallString = "_dafny.areEqual"; break;
        case BinaryExpr.ResolvedOpcode.ProperSubset:
        case BinaryExpr.ResolvedOpcode.ProperMultiSubset:
          callString = "IsProperSubsetOf"; break;
        case BinaryExpr.ResolvedOpcode.Subset:
        case BinaryExpr.ResolvedOpcode.MultiSubset:
          callString = "IsSubsetOf"; break;
        case BinaryExpr.ResolvedOpcode.Superset:
        case BinaryExpr.ResolvedOpcode.MultiSuperset:
          callString = "IsSupersetOf"; break;
        case BinaryExpr.ResolvedOpcode.ProperSuperset:
        case BinaryExpr.ResolvedOpcode.ProperMultiSuperset:
          callString = "IsProperSupersetOf"; break;
        case BinaryExpr.ResolvedOpcode.Disjoint:
        case BinaryExpr.ResolvedOpcode.MultiSetDisjoint:
        case BinaryExpr.ResolvedOpcode.MapDisjoint:
          callString = "IsDisjointFrom"; break;
        case BinaryExpr.ResolvedOpcode.InSet:
        case BinaryExpr.ResolvedOpcode.InMultiSet:
        case BinaryExpr.ResolvedOpcode.InMap:
          callString = "contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSet:
        case BinaryExpr.ResolvedOpcode.NotInMultiSet:
        case BinaryExpr.ResolvedOpcode.NotInMap:
          preOpString = "!"; callString = "contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.Union:
        case BinaryExpr.ResolvedOpcode.MultiSetUnion:
          callString = "Union"; break;
        case BinaryExpr.ResolvedOpcode.Intersection:
        case BinaryExpr.ResolvedOpcode.MultiSetIntersection:
          callString = "Intersect"; break;
        case BinaryExpr.ResolvedOpcode.SetDifference:
        case BinaryExpr.ResolvedOpcode.MultiSetDifference:
          callString = "Difference"; break;

        case BinaryExpr.ResolvedOpcode.ProperPrefix:
          staticCallString = "_dafny.Seq.IsProperPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Prefix:
          staticCallString = "_dafny.Seq.IsPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Concat:
          staticCallString = "_dafny.Seq.Concat"; break;
        case BinaryExpr.ResolvedOpcode.InSeq:
          staticCallString = "_dafny.Seq.contains"; reverseArguments = true; break;
        case BinaryExpr.ResolvedOpcode.NotInSeq:
          preOpString = "!"; callString = "contains"; reverseArguments = true; break;

        default:
          Contract.Assert(false); throw new cce.UnreachableException();  // unexpected binary expression
      }
    }

    protected override void EmitIsZero(string varName, TargetWriter wr) {
      wr.Write("{0}.isZero()", varName);
    }

    protected override void EmitConversionExpr(ConversionExpr e, bool inLetExprBody, TargetWriter wr) {
      if (e.E.Type.IsNumericBased(Type.NumericPersuation.Int) || e.E.Type.IsBitVectorType || e.E.Type.IsCharType) {
        if (e.ToType.IsNumericBased(Type.NumericPersuation.Real)) {
          // (int or bv) -> real
          Contract.Assert(AsNativeType(e.ToType) == null);
          wr.Write("new _dafny.BigRational(");
          if (AsNativeType(e.E.Type) != null) {
            wr.Write("new BigNumber");
          }
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(", new BigNumber(1))");
        } else if (e.ToType.IsCharType) {
          wr.Write("String.fromCharCode(");
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(".toNumber())");
        } else {
          // (int or bv or char) -> (int or bv or ORDINAL)
          var fromNative = AsNativeType(e.E.Type);
          var toNative = AsNativeType(e.ToType);
          if (fromNative != null && toNative != null) {
            // from a native, to a native -- simple!
           TrExpr(e.E, wr, inLetExprBody);
          } else if (e.E.Type.IsCharType) {
            Contract.Assert(fromNative == null);
            if (toNative == null) {
              // char -> big-integer (int or bv or ORDINAL)
              wr.Write("new BigNumber(");
              TrParenExpr(e.E, wr, inLetExprBody);
              wr.Write(".charCodeAt(0))");
            } else {
              // char -> native
              TrParenExpr(e.E, wr, inLetExprBody);
              wr.Write(".charCodeAt(0)");
            }
          } else if (fromNative == null && toNative == null) {
            // big-integer (int or bv) -> big-integer (int or bv or ORDINAL), so identity will do
            TrExpr(e.E, wr, inLetExprBody);
          } else if (fromNative != null && toNative == null) {
            // native (int or bv) -> big-integer (int or bv)
            wr.Write("new BigNumber");
            TrParenExpr(e.E, wr, inLetExprBody);
          } else {
            // any (int or bv) -> native (int or bv)
            // Consider some optimizations
            var literal = PartiallyEvaluate(e.E);
            UnaryOpExpr u = e.E.Resolved as UnaryOpExpr;
            MemberSelectExpr m = e.E.Resolved as MemberSelectExpr;
            if (literal != null) {
              // Optimize constant to avoid intermediate BigInteger
              wr.Write("(" + literal  + ")");
            } else if (u != null && u.Op == UnaryOpExpr.Opcode.Cardinality) {
              // Optimize .Count to avoid intermediate BigInteger
              TrParenExpr(u.E, wr, inLetExprBody);
              wr.Write(".length");
            } else if (m != null && m.MemberName == "Length" && m.Obj.Type.IsArrayType) {
              // Optimize .Length to avoid intermediate BigInteger
              TrParenExpr(m.Obj, wr, inLetExprBody);
              wr.Write(".length");
            } else {
              // no optimization applies; use the standard translation
              TrParenExpr(e.E, wr, inLetExprBody);
              wr.Write(".toNumber()");
            }

          }
        }
      } else if (e.E.Type.IsNumericBased(Type.NumericPersuation.Real)) {
        Contract.Assert(AsNativeType(e.E.Type) == null);
        if (e.ToType.IsNumericBased(Type.NumericPersuation.Real)) {
          // real -> real
          Contract.Assert(AsNativeType(e.ToType) == null);
          TrExpr(e.E, wr, inLetExprBody);
        } else {
          // real -> (int or bv)
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(".toBigNumber()");
          if (AsNativeType(e.ToType) != null) {
            wr.Write(".toNumber()");
          }
        }
      } else {
        Contract.Assert(e.E.Type.IsBigOrdinalType);
        Contract.Assert(e.ToType.IsNumericBased(Type.NumericPersuation.Int));
        // identity will do
        TrExpr(e.E, wr, inLetExprBody);
      }
    }

    protected override void EmitCollectionDisplay(CollectionType ct, Bpl.IToken tok, List<Expression> elements, bool inLetExprBody, TargetWriter wr) {
      if (ct is SetType) {
        wr.Write("_dafny.Set.fromElements");
        TrExprList(elements, wr, inLetExprBody);
      } else if (ct is MultiSetType) {
        wr.Write("_dafny.MultiSet.fromElements");
        TrExprList(elements, wr, inLetExprBody);
      } else {
        Contract.Assert(ct is SeqType);  // follows from precondition
        var wrElements = new TargetWriter(wr.IndentLevel);
        if (ct.Arg.IsCharType) {
          // We're really constructing a string.
          // TODO: It may be that ct.Arg is a type parameter that may stand for char. We currently don't catch that case here.
          wr.Write("[");
          wr.Append(wrElements);
          wr.Write("].join(\"\")");
        } else {
          wr.Write("_dafny.Seq.of(");
          wr.Append(wrElements);
          wr.Write(")");
        }
        string sep = "";
        foreach (var e in elements) {
          wrElements.Write(sep);
          TrExpr(e, wrElements, inLetExprBody);
          sep = ", ";
        }
      }
    }

    protected override void EmitMapDisplay(MapType mt, Bpl.IToken tok, List<ExpressionPair> elements, bool inLetExprBody, TargetWriter wr) {
      wr.Write("_dafny.Map.of(");
      string sep = "";
      foreach (ExpressionPair p in elements) {
        wr.Write(sep);
        wr.Write("[");
        TrExpr(p.A, wr, inLetExprBody);
        wr.Write(",");
        TrExpr(p.B, wr, inLetExprBody);
        wr.Write("]");
        sep = ", ";
      }
      wr.Write(")");
    }

    protected override void EmitCollectionBuilder_New(CollectionType ct, Bpl.IToken tok, TargetWriter wr) {
      if (ct is SetType) {
        wr.Write("new _dafny.Set()");
      } else if (ct is MultiSetType) {
        wr.Write("new _dafny.MultiSet()");
      } else if (ct is MapType) {
        wr.Write("new _dafny.Map()");
      } else {
        Contract.Assume(false);  // unepxected collection type
      }
    }

    protected override void EmitCollectionBuilder_Add(CollectionType ct, string collName, Expression elmt, bool inLetExprBody, TargetWriter wr) {
      Contract.Assume(ct is SetType || ct is MultiSetType);  // follows from precondition
      wr.Indent();
      wr.Write("{0}.add(", collName);
      TrExpr(elmt, wr, inLetExprBody);
      wr.WriteLine(");");
    }

    protected override TargetWriter EmitMapBuilder_Add(MapType mt, Bpl.IToken tok, string collName, Expression term, bool inLetExprBody, TargetWriter wr) {
      wr.Indent();
      wr.Write("{0}.push([", collName);
      var termLeftWriter = new TargetWriter(wr.IndentLevel);
      wr.Append(termLeftWriter);
      wr.Write(",");
      TrExpr(term, wr, inLetExprBody);
      wr.WriteLine("]);");
      return termLeftWriter;
    }

    protected override string GetCollectionBuilder_Build(CollectionType ct, Bpl.IToken tok, string collName, TargetWriter wr) {
      // collections are built in place
      return collName;
    }

    protected override void EmitSingleValueGenerator(Expression e, bool inLetExprBody, string type, TargetWriter wr) {
      TrParenExpr("_dafny.SingleValue", e, wr, inLetExprBody);
    }

    // ----- Target compilation and execution -------------------------------------------------------------

    public override bool CompileTargetProgram(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string/*?*/ targetFilename, ReadOnlyCollection<string> otherFileNames,
      bool hasMain, bool runAfterCompile, TextWriter outputWriter, out object compilationResult) {
      compilationResult = null;
      if (!DafnyOptions.O.RunAfterCompile || callToMain == null) {
        // compile now
        return SendToNewGoProcess(dafnyProgramName, targetProgramText, null, targetFilename, otherFileNames, outputWriter);
      } else {
        // Since the program is to be run soon, nothing further is done here. Any compilation errors (that is, any errors
        // in the emitted program--this should never happen if the compiler itself is correct) will be reported as 'node'
        // will run the program.
        return true;
      }
    }

    public override bool RunTargetProgram(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string targetFilename, ReadOnlyCollection<string> otherFileNames,
      object compilationResult, TextWriter outputWriter) {

      return SendToNewGoProcess(dafnyProgramName, targetProgramText, callToMain, targetFilename, otherFileNames, outputWriter);
    }

    bool SendToNewGoProcess(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string targetFilename, ReadOnlyCollection<string> otherFileNames,
      TextWriter outputWriter) {
      Contract.Requires(targetFilename != null || otherFileNames.Count == 0);

      var args = "run " + (targetFilename != null && otherFileNames.Count == 0 ? targetFilename : "");
      var psi = new ProcessStartInfo("go", args) {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
      };

      try {
        using (var nodeProcess = Process.Start(psi)) {
          if (args == "") {
            foreach (var filename in otherFileNames) {
              WriteFromFile(filename, nodeProcess.StandardInput);
            }
            nodeProcess.StandardInput.Write(targetProgramText);
            if (callToMain != null) {
              nodeProcess.StandardInput.Write(callToMain);
            }
            nodeProcess.StandardInput.Flush();
            nodeProcess.StandardInput.Close();
          }
          nodeProcess.WaitForExit();
          return nodeProcess.ExitCode == 0;
        }
      } catch (System.ComponentModel.Win32Exception e) {
        outputWriter.WriteLine("Error: Unable to start go ({0}): {1}", psi.FileName, e.Message);
        return false;
      }
    }
  }
}
