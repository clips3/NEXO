using System;

namespace Nexo.AutoCAD
{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class NexoRibbonCommandAttribute : Attribute
    {
        public NexoRibbonCommandAttribute(
            string commandName,
            string panelTitle,
            string buttonText,
            string toolTip,
            string iconFileName)
        {
            CommandName = commandName;
            PanelTitle = panelTitle;
            ButtonText = buttonText;
            ToolTip = toolTip;
            IconFileName = iconFileName;
        }

        public string CommandName { get; }
        public string PanelTitle { get; }
        public string ButtonText { get; }
        public string ToolTip { get; }
        public string IconFileName { get; }
    }
}
