using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(Nexo.AutoCAD.AutoBlockCommand))]

namespace Nexo.AutoCAD
{
    public sealed class AutoBlockCommand
    {
        [NexoRibbonCommand(
            "BLOCKREPLACERANDOM",
            "Blocos",
            "Auto Block",
            "Agrupa os objetos selecionados em um bloco com nome exclusivo e ponto-base no centro geométrico.",
            "icon_auto_block.png")]
        [CommandMethod("BLOCKREPLACERANDOM", CommandFlags.UsePickSet)]
        public void CreateRandomNamedBlock()
        {
            Document document = Application.DocumentManager.MdiActiveDocument;
            if (document == null)
                return;

            Editor editor = document.Editor;
            try
            {
                SelectionSet selection = GetSelection(editor);
                if (selection == null || selection.Count == 0)
                {
                    editor.WriteMessage("\n[NEXO] Nenhum objeto selecionado para converter em bloco.");
                    return;
                }

                ObjectId[] selectedIds = selection.GetObjectIds();
                Database database = document.Database;
                Point3d center;
                ObjectId[] eligibleIds;
                if (!TryGetSelectionCenter(database, selectedIds, out center, out eligibleIds))
                {
                    editor.WriteMessage("\n[NEXO] Não foi possível calcular o centro geométrico dos objetos selecionados no espaço atual.");
                    return;
                }

                if (eligibleIds.Length != selectedIds.Length)
                {
                    editor.WriteMessage("\n[NEXO] Apenas objetos do espaço atual podem ser convertidos. A seleção foi ajustada.");
                }

                string blockName = CreateUniqueBlockName(database);
                Point3d centerInUcs = center.TransformBy(editor.CurrentUserCoordinateSystem.Inverse());
                string point = FormatPoint(centerInUcs);
                SelectionSet eligibleSelection = SelectionSet.FromObjectIds(eligibleIds);
                editor.Command("_.-BLOCK", blockName, point, eligibleSelection, "");
                editor.Command("_.-INSERT", blockName, point, 1.0, 1.0, 0.0);
                editor.WriteMessage("\n[NEXO] Convertidos " + eligibleIds.Length + " objeto(s) no bloco " + blockName + ".");
            }
            catch (System.Exception exception)
            {
                editor.WriteMessage("\n[NEXO] Falha em BLOCKREPLACERANDOM: " + exception.Message);
            }
        }

        internal static SelectionSet GetSelection(Editor editor)
        {
            PromptSelectionResult implied = editor.SelectImplied();
            if (implied.Status == PromptStatus.OK && implied.Value != null && implied.Value.Count > 0)
                return implied.Value;

            PromptSelectionResult selected = editor.GetSelection();
            return selected.Status == PromptStatus.OK ? selected.Value : null;
        }

        internal static bool TryGetSelectionCenter(
            Database database,
            ObjectId[] selectedIds,
            out Point3d center,
            out ObjectId[] eligibleIds)
        {
            center = Point3d.Origin;
            List<ObjectId> accepted = new List<ObjectId>();
            bool hasExtents = false;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            double maxZ = double.NegativeInfinity;
            ObjectId currentSpaceId = database.CurrentSpaceId;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in selectedIds)
                {
                    if (id.IsNull || !id.IsValid || id.IsErased)
                        continue;

                    Entity entity = transaction.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || entity.OwnerId != currentSpaceId)
                        continue;

                    accepted.Add(id);
                    try
                    {
                        Extents3d extents = entity.GeometricExtents;
                        minX = Math.Min(minX, extents.MinPoint.X);
                        minY = Math.Min(minY, extents.MinPoint.Y);
                        minZ = Math.Min(minZ, extents.MinPoint.Z);
                        maxX = Math.Max(maxX, extents.MaxPoint.X);
                        maxY = Math.Max(maxY, extents.MaxPoint.Y);
                        maxZ = Math.Max(maxZ, extents.MaxPoint.Z);
                        hasExtents = true;
                    }
                    catch
                    {
                        // Some proxy/custom entities do not expose geometric extents.
                    }
                }

                transaction.Commit();
            }

            eligibleIds = accepted.ToArray();
            if (!hasExtents || accepted.Count == 0)
                return false;

            center = new Point3d(
                (minX + maxX) / 2.0,
                (minY + maxY) / 2.0,
                (minZ + maxZ) / 2.0);
            return true;
        }

        internal static string CreateUniqueBlockName(Database database)
        {
            string baseName = "RndBlk-" + DateTime.Now.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
            string blockName = baseName;
            int suffix = 1;

            using (Transaction transaction = database.TransactionManager.StartTransaction())
            {
                BlockTable blockTable = transaction.GetObject(database.BlockTableId, OpenMode.ForRead) as BlockTable;
                while (blockTable != null && blockTable.Has(blockName))
                {
                    blockName = baseName + "-" + suffix.ToString(CultureInfo.InvariantCulture);
                    suffix++;
                }
                transaction.Commit();
            }

            return blockName;
        }

        internal static string FormatPoint(Point3d point)
        {
            return point.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                   point.Y.ToString("R", CultureInfo.InvariantCulture) + "," +
                   point.Z.ToString("R", CultureInfo.InvariantCulture);
        }

    }
}
