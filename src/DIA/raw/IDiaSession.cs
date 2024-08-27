using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace VisualStudioProvider.PDB.raw
{
    [ComImport, Guid("67138B34-79CD-4b42-B74A-A18ADBB799DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IDiaSession
    {
	    int get_loadAddress(ref ulong pRetVal);
	    int put_loadAddress(ulong NewVal);
	    int get_globalScope(out IDiaSymbol pRetVal);
	    int getEnumTables(out IDiaEnumTables ppEnumTables);
	    int getSymbolsByAddr(out IDiaEnumSymbolsByAddr ppEnumbyAddr);
	    int findChildren(IDiaSymbol parent, SymTagEnum symtag, string name, uint compareFlags, out IDiaEnumSymbols ppResult);
	    int findChildrenEx(IDiaSymbol parent, SymTagEnum symtag, string name, uint compareFlags, out IDiaEnumSymbols ppResult);
	    int findChildrenExByAddr(IDiaSymbol parent, SymTagEnum symtag, string name, uint compareFlags, uint isect, uint offset, out IDiaEnumSymbols ppResult);
	    int findChildrenExByVA(IDiaSymbol parent, SymTagEnum symtag, string name, uint compareFlags, ulong va, out IDiaEnumSymbols ppResult);
	    int findChildrenExByRVA(IDiaSymbol parent, SymTagEnum symtag, string name, uint compareFlags, uint rva, out IDiaEnumSymbols ppResult);
	    int findSymbolByAddr(uint isect, uint offset, SymTagEnum symtag, IDiaSymbol[] ppSymbol);
	    int findSymbolByRVA(uint rva, SymTagEnum symtag, out IDiaSymbol ppSymbol);
	    int findSymbolByVA(ulong va, SymTagEnum symtag, IDiaSymbol[] ppSymbol);
	    int findSymbolByToken(uint token, SymTagEnum symtag, IDiaSymbol[] ppSymbol);
	    int symsAreEquiv(IDiaSymbol symbolA, IDiaSymbol symbolB);
	    int symbolById(uint id, IDiaSymbol[] ppSymbol);
	    int findSymbolByRVAEx(uint rva, SymTagEnum symtag, IDiaSymbol[] ppSymbol, ref int displacement);
	    int findSymbolByVAEx(ulong va, SymTagEnum symtag, IDiaSymbol[] ppSymbol, ref int displacement);
	    int findFile(IDiaSymbol pCompiland, string name, uint compareFlags, out IDiaEnumSourceFiles ppResult);
	    int findFileById(uint uniqueId, out IDiaSourceFile ppResult);
	    int findLines(IDiaSymbol compiland, IDiaSourceFile file, out IDiaEnumLineNumbers ppResult);
	    int findLinesByAddr(uint seg, uint offset, uint length, out IDiaEnumLineNumbers ppResult);
	    int findLinesByRVA(uint rva, uint length, out IDiaEnumLineNumbers ppResult);
	    int findLinesByVA(ulong va, uint length, out IDiaEnumLineNumbers ppResult);
	    int findLinesByLinenum(IDiaSymbol compiland, IDiaSourceFile file, uint linenum, uint column, out IDiaEnumLineNumbers ppResult);
	    int findInjectedSource(string srcFile, out IDiaEnumInjectedSources ppResult);
	    int getEnumDebugStreams(out IDiaEnumDebugStreams ppEnumDebugStreams);
    }
}
