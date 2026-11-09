using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;

namespace Nexo.AutoCAD
{
    internal static class WallBlockLibrary
    {
        internal static void ImportOpenings(Database target, IEnumerable<DetectedWall> openings)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DetectedWall opening in openings)
                names.Add(opening.Opening == OpeningKind.Door ? "ISO_Porta2" : "ISO_Janela");
            string directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            ImportMissing(target, Path.Combine(directory ?? string.Empty, "Resources", "NexoOpeningBlocks.dwg"), new List<string>(names).ToArray());
        }
        internal static void ImportMissing(Database target, string libraryPath = null, string[] requiredNames = null)
        {
            string[] names = requiredNames ?? new[] { "ISO_Parede", "ISO_Laje" };
            List<string> missing = new List<string>();
            using (Transaction transaction = target.TransactionManager.StartTransaction())
            {
                BlockTable table = transaction.GetObject(target.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (table == null) throw new InvalidOperationException("Não foi possível acessar os blocos do desenho.");
                foreach (string name in names) if (!table.Has(name)) missing.Add(name);
                transaction.Commit();
            }
            if (missing.Count == 0) return;

            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                libraryPath = Path.Combine(assemblyDirectory ?? string.Empty, "Resources", "NexoWallBlocks.dwg");
            }
            if (!File.Exists(libraryPath))
                throw new InvalidOperationException("A biblioteca dos blocos de parede e laje do NEXO não foi encontrada: " + libraryPath);

            using (Database source = new Database(false, true))
            {
                source.ReadDwgFile(libraryPath, FileOpenMode.OpenForReadAndAllShare, false, string.Empty);
                source.CloseInput(true);
                ObjectIdCollection sourceIds = new ObjectIdCollection();
                using (Transaction transaction = source.TransactionManager.StartTransaction())
                {
                    BlockTable table = transaction.GetObject(source.BlockTableId, OpenMode.ForRead) as BlockTable;
                    foreach (string name in missing)
                    {
                        if (table == null || !table.Has(name))
                            throw new InvalidOperationException("A biblioteca NEXO não contém o bloco " + name + ".");
                        sourceIds.Add(table[name]);
                    }
                    transaction.Commit();
                }
                IdMapping mapping = new IdMapping();
                source.WblockCloneObjects(sourceIds, target.BlockTableId, mapping, DuplicateRecordCloning.Ignore, false);
            }
        }
    }
}
