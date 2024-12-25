using System;

namespace GuiLabs.PEFile;

public enum CodedIndex
{
    TypeDefOrRef,
    HasConstant,
    HasCustomAttribute,
    HasFieldMarshal,
    HasDeclSecurity,
    MemberRefParent,
    HasSemantics,
    MethodDefOrRef,
    MemberForwarded,
    Implementation,
    CustomAttributeType,
    ResolutionScope,
    TypeOrMethodDef,
    HasCustomDebugInformation,
}

internal static class CodedIndexExtensions
{
    public static int GetSize(this CodedIndex self, Func<Table, int> counter)
    {
        int bits;
        Table[] tables;

        switch (self)
        {
            case CodedIndex.TypeDefOrRef:
                bits = 2;
                tables = new[] { Table.TypeDef, Table.TypeRef, Table.TypeSpec };
                break;
            case CodedIndex.HasConstant:
                bits = 2;
                tables = new[] { Table.Field, Table.Param, Table.Property };
                break;
            case CodedIndex.HasCustomAttribute:
                bits = 5;
                tables = new[] {
                    Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param, Table.InterfaceImpl, Table.MemberRef,
                    Table.Module, Table.DeclSecurity, Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                    Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File, Table.ExportedType,
                    Table.ManifestResource, Table.GenericParam, Table.GenericParamConstraint, Table.MethodSpec,
                };
                break;
            case CodedIndex.HasFieldMarshal:
                bits = 1;
                tables = new[] { Table.Field, Table.Param };
                break;
            case CodedIndex.HasDeclSecurity:
                bits = 2;
                tables = new[] { Table.TypeDef, Table.Method, Table.Assembly };
                break;
            case CodedIndex.MemberRefParent:
                bits = 3;
                tables = new[] { Table.TypeDef, Table.TypeRef, Table.ModuleRef, Table.Method, Table.TypeSpec };
                break;
            case CodedIndex.HasSemantics:
                bits = 1;
                tables = new[] { Table.Event, Table.Property };
                break;
            case CodedIndex.MethodDefOrRef:
                bits = 1;
                tables = new[] { Table.Method, Table.MemberRef };
                break;
            case CodedIndex.MemberForwarded:
                bits = 1;
                tables = new[] { Table.Field, Table.Method };
                break;
            case CodedIndex.Implementation:
                bits = 2;
                tables = new[] { Table.File, Table.AssemblyRef, Table.ExportedType };
                break;
            case CodedIndex.CustomAttributeType:
                bits = 3;
                tables = new[] { Table.Method, Table.MemberRef };
                break;
            case CodedIndex.ResolutionScope:
                bits = 2;
                tables = new[] { Table.Module, Table.ModuleRef, Table.AssemblyRef, Table.TypeRef };
                break;
            case CodedIndex.TypeOrMethodDef:
                bits = 1;
                tables = new[] { Table.TypeDef, Table.Method };
                break;
            case CodedIndex.HasCustomDebugInformation:
                bits = 5;
                tables = new[] {
                    Table.Method, Table.Field, Table.TypeRef, Table.TypeDef, Table.Param, Table.InterfaceImpl, Table.MemberRef,
                    Table.Module, Table.DeclSecurity, Table.Property, Table.Event, Table.StandAloneSig, Table.ModuleRef,
                    Table.TypeSpec, Table.Assembly, Table.AssemblyRef, Table.File, Table.ExportedType,
                    Table.ManifestResource, Table.GenericParam, Table.GenericParamConstraint, Table.MethodSpec,
                    Table.Document, Table.LocalScope, Table.LocalVariable, Table.LocalConstant, Table.ImportScope,
                };
                break;
            default:
                throw new ArgumentException();
        }

        int max = 0;

        for (int i = 0; i < tables.Length; i++)
        {
            max = System.Math.Max(counter(tables[i]), max);
        }

        return max < (1 << (16 - bits)) ? 2 : 4;
    }
}