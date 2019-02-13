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
  public class JavaScriptCompiler : Compiler {
    public JavaScriptCompiler(ErrorReporter reporter)
    : base(reporter) {
    }

    public override string TargetLanguage => "JavaScript";

    protected override void EmitHeader(Program program, TargetWriter wr) {
      wr.WriteLine("// Dafny program {0} compiled into JavaScript", program.Name);
      ReadRuntimeSystem("DafnyRuntime.js", wr);
    }

    public override void EmitCallToMain(Method mainMethod, TextWriter wr) {
      wr.WriteLine("{0}.{1}();", mainMethod.EnclosingClass.FullCompileName, IdName(mainMethod));
    }
      
    protected override BlockTargetWriter CreateModule(string moduleName, TargetWriter wr) {
      var w = wr.NewBigBlock(string.Format("let {0} = (function()", moduleName), ")(); // end of module " + moduleName);
      w.Indent();
      w.WriteLine("let $module = {};");
      w.BodySuffix = string.Format("{0}return $module;{1}", w.IndentString, w.NewLine);
      return w;
    }

    protected override string GetHelperModuleName() => "_dafny";

    protected override BlockTargetWriter CreateClass(string name, List<TypeParameter>/*?*/ typeParameters, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, TargetWriter wr) {
      wr.Indent();
      var w = wr.NewBlock(string.Format("$module.{0} = class {0}", name), ";");
      w.Indent();
      instanceFieldsWriter = w.NewBlock("constructor ()");
      return w;
    }

    protected override BlockTargetWriter CreateTrait(string name, List<Type>/*?*/ superClasses, Bpl.IToken tok, out TargetWriter instanceFieldsWriter, out TargetWriter staticMemberWriter, TargetWriter wr) {
      wr.Indent();
      var w = wr.NewBlock(string.Format("$module.{0} = class {0}", IdProtect(name)), ";");
      w.Indent();
      instanceFieldsWriter = w.NewBlock("constructor ()");
      staticMemberWriter = w;
      return w;
    }

    protected override void DeclareDatatype(DatatypeDecl dt, TargetWriter wr) {
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
      //   toString() {
      //     ...
      //   }
      //   equals(other) {
      //     ...
      //   }
      //   static get Default() {
      //     if (this.theDefault === undefined) {
      //       this.theDefault = this.create_CtorK(...);
      //     }
      //     return this.theDefault;
      //   }
      // }
      // TODO: need Default member (also for co-datatypes)
      // TODO: if HasFinitePossibleValues, need enumerator of values

      string DtT = dt.CompileName;
      string DtT_protected = IdProtect(DtT);

      wr.Indent();
      // from here on, write everything into the new block created here:
      wr = wr.NewNamedBlock("$module.{0} = class {0}", DtT_protected);

      wr.Indent();
      wr.WriteLine("constructor(tag) { this.$tag = tag; }");


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
        // static create_Ctor0(params) { return {$tag:0, p0: pararms0, p1: params1, ...}; }
        wr.Indent();
        wr.Write("static create_{0}(", ctor.CompileName);
        wr.Write(Util.Comma(argNames, nm => nm));
        var w = wr.NewBlock(")");
        w.Indent();
        w.WriteLine("let $dt = new {0}({1});", DtT_protected, i);
        foreach (var arg in argNames) {
          w.Indent();
          w.WriteLine("$dt.{0} = {0};", arg);
        }
        w.Indent();
        w.WriteLine("return $dt;");
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

      if (dt is IndDatatypeDecl && !(dt is TupleTypeDecl)) {
        // toString method
        wr.Indent();
        var w = wr.NewBlock("toString()");
        i = 0;
        foreach (var ctor in dt.Ctors) {
          var cw = EmitIf(string.Format("this.$tag === {0}", i), true, w);
          cw.Indent();
          cw.Write("return \"{0}.{1}\"", dt.Name, ctor.Name);
          var sep = " + \"(\" + ";
          var anyFormals = false;
          var k = 0;
          foreach (var arg in ctor.Formals) {
            if (!arg.IsGhost) {
              anyFormals = true;
              cw.Write("{0}this.{1}.toString()", sep, FormalName(arg, k));
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
        w = w.NewBlock("");
        w.Indent();
        w.WriteLine("return \"<unexpected>\";");
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
                  guard.Write(" && this.{0} === oth.{0}", nm);
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

      // Default getter
      wr.Indent();
      using (var w = wr.NewBlock("static get Default()")) {
        using (var dw = EmitIf("this.theDefault === undefined", false, w)) {
          DatatypeCtor defaultCtor;
          if (dt is IndDatatypeDecl) {
            defaultCtor = ((IndDatatypeDecl)dt).DefaultCtor;
          } else {
            defaultCtor = ((CoDatatypeDecl)dt).Ctors[0];  // pick any one of them (but pick must be the same as in InitializerIsKnown and HasZeroInitializer)
          }

          dw.Indent();
          dw.Write("this.theDefault = this.create_{0}(", defaultCtor.CompileName);
          string sep = "";
          foreach (Formal f in defaultCtor.Formals) {
            if (!f.IsGhost) {
              dw.Write("{0}{1}", sep, DefaultValue(f.Type, dw, f.tok));
              sep = ", ";
            }
          }
          dw.WriteLine(");");
        }
        EmitReturnExpr("this.theDefault", w);
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
        var witness = new TargetWriter();
        if (nt.NativeType == null) {
          TrExpr(nt.Witness, witness, false);
        } else {
          TrParenExpr(nt.Witness, witness, false);
          witness.Write(".toNumber()");
        }
        DeclareField("Witness", true, true, nt.BaseType, nt.tok, witness.ToString(), w);
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
      wr.Write("{0}{1}(", m.IsStatic ? "static " : "", IdName(m));
      int nIns = WriteFormals("", m.Ins, wr);
      var w = wr.NewBlock(")");

      if (!m.IsStatic) {
        w.Indent(); w.WriteLine("let _this = this;");
      }
      if (m.IsTailRecursive) {
        w.Indent();
        w = w.NewBlock("TAIL_CALL_START: while (true)");
      }
      var r = new TargetWriter(w.IndentLevel);
      EmitReturn(m.Outs, r);
      w.BodySuffix = r.ToString();
      return w;
    }

    protected override BlockTargetWriter/*?*/ CreateFunction(string name, List<TypeParameter>/*?*/ typeArgs, List<Formal> formals, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, MemberDecl member, TargetWriter wr) {
      if (!createBody) {
        return null;
      }
      wr.Indent();
      wr.Write("{0}{1}(", isStatic ? "static " : "", name);
      int nIns = WriteFormals("", formals, wr);
      var w = wr.NewBlock(")", ";");
      if (!isStatic) {
        w.Indent(); w.WriteLine("let _this = this;");
      }
      return w;
    }

    protected override BlockTargetWriter/*?*/ CreateGetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, TargetWriter wr) {
      if (createBody) {
        wr.Indent();
        wr.Write("{0}get {1}()", isStatic ? "static " : "", name);
        var w = wr.NewBlock("", ";");
        if (!isStatic) {
          w.Indent(); w.WriteLine("let _this = this;");
        }
        return w;
      } else {
        return null;
      }
    }

    protected override BlockTargetWriter/*?*/ CreateGetterSetter(string name, Type resultType, Bpl.IToken tok, bool isStatic, bool createBody, out TargetWriter setterWriter, TargetWriter wr) {
      if (createBody) {
        wr.Indent();
        wr.Write("{0}get {1}()", isStatic ? "static " : "", name);
        var wGet = wr.NewBlock("", ";");
        if (!isStatic) {
          wGet.Indent(); wGet.WriteLine("let _this = this;");
        }

        wr.Indent();
        wr.Write("{0}set {1}(value)", isStatic ? "static " : "", name);
        var wSet = wr.NewBlock("", ";");
        if (!isStatic) {
          wSet.Indent(); wSet.WriteLine("let _this = this;");
        }

        setterWriter = wSet;
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

    protected override string TypeName(Type type, TextWriter wr, Bpl.IToken tok) {
      Contract.Requires(type != null);
      Contract.Ensures(Contract.Result<string>() != null);

      var xType = type.NormalizeExpand();
      if (xType is TypeProxy) {
        // unresolved proxy; just treat as ref, since no particular type information is apparently needed for this type
        return "object";
      }

      if (xType is BoolType) {
        return "bool";
      } else if (xType is CharType) {
        return "char";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "BigNumber";
      } else if (xType is RealType) {
        return "Dafny.BigRational";
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
        return "object";
      } else if (xType.IsArrayType) {
        ArrayClassDecl at = xType.AsArrayType;
        Contract.Assert(at != null);  // follows from type.IsArrayType
        Type elType = UserDefinedType.ArrayElementType(xType);
        string typeNameSansBrackets, brackets;
        TypeName_SplitArrayName(elType, wr, tok, out typeNameSansBrackets, out brackets);
        return typeNameSansBrackets + TypeNameArrayBrackets(at.Dims) + brackets;
      } else if (xType is UserDefinedType) {
        var udt = (UserDefinedType)xType;
        var s = udt.FullCompileName;
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

    public override string TypeInitializationValue(Type type, TextWriter/*?*/ wr, Bpl.IToken/*?*/ tok) {
      var xType = type.NormalizeExpandKeepConstraints();
      if (xType is BoolType) {
        return "false";
      } else if (xType is CharType) {
        return "'D'";
      } else if (xType is IntType || xType is BigOrdinalType) {
        return "new BigNumber(0)";
      } else if (xType is RealType) {
        return "_dafny.BigRational.ZERO";
      } else if (xType is BitvectorType) {
        var t = (BitvectorType)xType;
        return t.NativeType != null ? "0" : "new BigNumber(0)";
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
        return string.Format("_dafny.Default(\"{0}\")", TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok));  // TODO
      }
      var cl = udt.ResolvedClass;
      Contract.Assert(cl != null);
      if (cl is NewtypeDecl) {
        var td = (NewtypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.NativeType != null) {
          return "0";
        } else {
          return TypeInitializationValue(td.BaseType, wr, tok);
        }
      } else if (cl is SubsetTypeDecl) {
        var td = (SubsetTypeDecl)cl;
        if (td.Witness != null) {
          return TypeName_UDT(udt.FullCompileName, udt.TypeArgs, wr, udt.tok) + ".Witness";
        } else if (td.WitnessKind == SubsetTypeDecl.WKind.Special) {
          // WKind.Special is only used with -->, ->, and non-null types:
          Contract.Assert(ArrowType.IsPartialArrowTypeName(td.Name) || ArrowType.IsTotalArrowTypeName(td.Name) || td is NonNullTypeDecl);
          if (ArrowType.IsPartialArrowTypeName(td.Name)) {
            return string.Format("null)");
          } else if (ArrowType.IsTotalArrowTypeName(td.Name)) {
            var rangeDefaultValue = TypeInitializationValue(udt.TypeArgs.Last(), wr, tok);
            // return the lambda expression ((Ty0 x0, Ty1 x1, Ty2 x2) => rangeDefaultValue)
            return string.Format("function () {{ return {0}; }}", rangeDefaultValue);
          } else if (((NonNullTypeDecl)td).Class is ArrayClassDecl) {
            // non-null array type; we know how to initialize them
            return "[]";
          } else {
            // non-null (non-array) type
            // even though the type doesn't necessarily have a known initializer, it could be that the the compiler needs to
            // lay down some bits to please the C#'s compiler's different definite-assignment rules.
            return "null";
          }
        } else {
          return TypeInitializationValue(td.RhsWithArgument(udt.TypeArgs), wr, tok);
        }
      } else if (cl is ClassDecl) {
        bool isHandle = true;
        if (Attributes.ContainsBool(cl.Attributes, "handle", ref isHandle) && isHandle) {
          return "0";
        } else {
          return "null";
        }
      } else if (cl is DatatypeDecl) {
        var s = udt.FullCompileName;
        // TODO: pass udt.TypeArgs as parameters
        return string.Format("{0}.Default", s);
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

    protected override string TypeName_Companion(Type type, TextWriter wr, Bpl.IToken tok) {
      var udt = type as UserDefinedType;
      if (udt != null && udt.ResolvedClass is TraitDecl) {
        if (udt.TypeArgs.Count != 0 && udt.TypeArgs.Exists(argType => argType.NormalizeExpand().IsObjectQ)) {
          // TODO: This is a restriction for .NET, but may not need to be a restriction for JavaScript
          Error(udt.tok, "compilation does not support type 'object' as a type parameter; consider introducing a ghost", wr);
        }
      }
      return TypeName(type, wr, tok);
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
      wr.Write("let {0}", name);
      if (leaveRoomForRhs) {
        Contract.Assert(rhs == null);  // follows from precondition
      } else if (rhs != null) {
        wr.WriteLine(" = {0};", rhs);
      } else {
        wr.WriteLine(";");
      }
    }

    protected override TargetWriter DeclareLocalVar(string name, Type/*?*/ type, Bpl.IToken/*?*/ tok, TargetWriter wr) {
      wr.Indent();
      wr.Write("let {0} = ", name);
      var w = new TargetWriter(wr.IndentLevel);
      wr.Append(w);
      wr.WriteLine(";");
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
      wr.Write("process.stdout.write(");
      TrParenExpr(arg, wr, false);
      // Annoyingly, BigNumber.toString() may return a string in scientific notation. To
      // prevent that, toFixed() is used. Note, however, that this does not catch the case
      // where "arg" denotes an integer but its type is some type parameter.
      if (arg.Type.IsIntegerType) {
        wr.WriteLine(".toFixed());");
      } else if (arg.Type.IsBitVectorType && AsNativeType(arg.Type) == null) {
        wr.WriteLine(".toFixed());");
      } else {
        wr.WriteLine(".toString());");
      }
    }

    protected override void EmitReturn(List<Formal> outParams, TargetWriter wr) {
      wr.Indent();
      if (outParams.Count == 0) {
        wr.WriteLine("return;");
      } else if (outParams.Count == 1) {
        wr.WriteLine("return {0};", IdName(outParams[0]));
      } else {
        wr.WriteLine("return [{0}];", Util.Comma(outParams, IdName));
      }
    }

    protected override TargetWriter CreateLabeledCode(string label, TargetWriter wr) {
      wr.Indent();
      return wr.NewNamedBlock("{0}:", label);
    }

    protected override void EmitBreak(string/*?*/ label, TargetWriter wr) {
      wr.Indent();
      if (label == null) {
        wr.WriteLine("break;");
      } else {
        wr.WriteLine("break {0};", label);
      }
    }

    protected override void EmitYield(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("yield null;");
    }

    protected override void EmitAbsurd(TargetWriter wr) {
      wr.Indent();
      wr.WriteLine("throw new Error('unexpected control point');");
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
      } else {
        return wr.NewBlockWithPrefix(")", "{0} = {1};", altBoundVarName, boundVar);
      }
    }

    // ----- Expressions -------------------------------------------------------------

    protected override void EmitNew(Type type, Bpl.IToken tok, CallStmt/*?*/ initCall, TargetWriter wr) {
      wr.Write("new {0}()", TypeName(type, wr, tok));
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
      if (-9007199254740991 <= i && i <= 9007199254740991) {
        wr.Write("new BigNumber({0})", i);
      } else {
        wr.Write("new BigNumber(\"{0}\")", i);
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

    protected override void EmitThis(TargetWriter wr) {
      wr.Write("_this");
    }

    protected override void EmitDatatypeValue(DatatypeValue dtv, string dtName, string ctorName, string arguments, TargetWriter wr) {
      var dt = dtv.Ctor.EnclosingDatatype;
      if (dt is TupleTypeDecl) {
        wr.Write("_dafny.Tuple.of({0})", arguments);
      } else {
        wr.Write("{0}.{1}.create_{2}({3})", dt.Module.CompileName, dtName, ctorName, arguments);
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
          compiledName = "ToBigInteger()";
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
      } else if (member is DatatypeDestructor dtor) {
        if (dtor.EnclosingClass is TupleTypeDecl) {
          wr.Write("[{0}]", dtor.Name);
        } else {
          wr.Write(".{0}", IdName(member));
        }
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
        wr.Write("({0}).{1}", source, dtorName);
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
          wr.Write(".length)");
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
          callString = "IsProperPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Prefix:
          callString = "IsPrefixOf"; break;
        case BinaryExpr.ResolvedOpcode.Concat:
          staticCallString = "_dafny.Concat"; break;
        case BinaryExpr.ResolvedOpcode.InSeq:
          callString = "contains"; reverseArguments = true; break;
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
          wr.Write("new Dafny.BigRational(");
          if (AsNativeType(e.E.Type) != null) {
            wr.Write("new BigNumber");
          }
          TrParenExpr(e.E, wr, inLetExprBody);
          wr.Write(", new BigNumber(1))");
        } else if (e.ToType.IsCharType) {
          wr.Write("(char)(");
          TrExpr(e.E, wr, inLetExprBody);
          wr.Write(")");
        } else {
          // (int or bv) -> (int or bv or ORDINAL)
          var fromNative = AsNativeType(e.E.Type);
          var toNative = AsNativeType(e.ToType);
          if (fromNative == null && toNative == null) {
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
              if (toNative.UpperBound <= new BigInteger(0x80000000U)) {
                wr.Write(".Count");
              } else {
                wr.Write(".LongCount");
              }
            } else if (m != null && m.MemberName == "Length" && m.Obj.Type.IsArrayType) {
              // Optimize .Length to avoid intermediate BigInteger
              TrParenExpr(m.Obj, wr, inLetExprBody);
              if (toNative.UpperBound <= new BigInteger(0x80000000U)) {
                wr.Write(".length");
              } else {
                wr.Write(".LongLength");
              }
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
          wr.Write(".ToBigInteger()");
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
        return SendToNewNodeProcess(dafnyProgramName, targetProgramText, null, targetFilename, otherFileNames, outputWriter);
      } else {
        // Since the program is to be run soon, nothing further is done here. Any compilation errors (that is, any errors
        // in the emitted program--this should never happen if the compiler itself is correct) will be reported as 'node'
        // will run the program.
        return true;
      }
    }

    public override bool RunTargetProgram(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string targetFilename, ReadOnlyCollection<string> otherFileNames,
      object compilationResult, TextWriter outputWriter) {

      return SendToNewNodeProcess(dafnyProgramName, targetProgramText, callToMain, targetFilename, otherFileNames, outputWriter);
    }

    bool SendToNewNodeProcess(string dafnyProgramName, string targetProgramText, string/*?*/ callToMain, string targetFilename, ReadOnlyCollection<string> otherFileNames,
      TextWriter outputWriter) {

      string args = "";
      if (targetFilename != null) {
        args += targetFilename;
        foreach (var s in otherFileNames) {
          args += " " + s;
        }
      } else {
        Contract.Assert(otherFileNames.Count == 0);  // according to the precondition
      }
      var psi = new ProcessStartInfo("node", args) {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = false,
        RedirectStandardError = false,
      };

      try {
        using (var nodeProcess = Process.Start(psi)) {
          if (targetFilename == null) {
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
        outputWriter.WriteLine("Error: Unable to start node.js ({0}): {1}", psi.FileName, e.Message);
        return false;
      }
    }
  }
}