using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace VisualStudioProvider.PDB.diaapi
{
    public static class Extensions
    {
        public static List<DiaSymbol> GetDesendants(this DiaSymbol symbol)
        {
            var symbols = new Dictionary<uint, DiaSymbol>();
            Action<DiaSymbol> recurse = null;

            recurse = (s) =>
            {
                if (s.Children != null)
                {
                    foreach (var child in s.Children)
                    {
                        var id = child.IndexId;

                        try
                        {
                            var debugInfo = child.DebugInfo;
                        }
                        catch (Exception ex)
                        {

                        }

                        if (!symbols.ContainsKey(id))
                        {
                            symbols.Add(id, child);

                            recurse(child);
                        }
                    }
                }
            };

            recurse(symbol);

            return symbols.Values.ToList();
        }

        public static List<DiaSymbol> GetDesendants(this DiaSymbol symbol, Func<DiaSymbol, bool> filter)
        {
            var symbols = new Dictionary<uint, DiaSymbol>();
            Action<DiaSymbol> recurse = null;

            recurse = (s) =>
            {
                if (s.Children != null)
                {
                    foreach (var child in s.Children.Where(filter))
                    {
                        var id = child.IndexId;

                        try
                        {
                            var debugInfo = child.DebugInfo;
                        }
                        catch (Exception ex)
                        {

                        }

                        if (!symbols.ContainsKey(id))
                        {
                            symbols.Add(id, child);

                            recurse(child);
                        }
                    }
                }
            };

            recurse(symbol);

            return symbols.Values.ToList();
        }

    }
}
