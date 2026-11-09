using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(Nexo.AutoCAD.PluginEntry))]

namespace Nexo.AutoCAD
{
    public sealed class PluginEntry : IExtensionApplication
    {
        public void Initialize()
        {
            NexoRibbonUi.Initialize();
        }

        public void Terminate()
        {
            NexoRibbonUi.Shutdown();
        }
    }
}
