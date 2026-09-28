using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace Plugin3.LinkChangeMonitor.Revit
{
    internal static class RibbonIconLoader
    {
        public static ImageSource Load(string name, int size)
        {
            try
            {
                byte[] embeddedBytes;
                if (EmbeddedIconData.TryGet((name ?? string.Empty) + size, out embeddedBytes))
                {
                    using (var memory = new MemoryStream(embeddedBytes, false))
                    {
                        var image = Read(memory);
                        BuildAI.Core.Logging.PluginLog.Info("ACC_RIBBON_ICON_LOADED name="+name+" size="+size+" source=embedded");
                        return image;
                    }
                }

                var asm = Assembly.GetExecutingAssembly();
                var suffix = $"Resources.icons.{name}{size}.png";
                var resource = asm.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                if (resource != null)
                {
                    using (var stream = asm.GetManifestResourceStream(resource))
                    {
                        if (stream != null) return Read(stream);
                    }
                }

                var path = Path.Combine(Path.GetDirectoryName(asm.Location), "Resources", "icons", name + size + ".png");
                if (File.Exists(path))
                {
                    using (var stream = File.OpenRead(path)) return Read(stream);
                }
            }
            catch (Exception ex) { BuildAI.Core.Logging.PluginLog.Error("ACC_RIBBON_ICON_LOAD_FAILED name="+name+" size="+size, ex); }
            BuildAI.Core.Logging.PluginLog.Info("ACC_RIBBON_ICON_FALLBACK name="+name+" size="+size);
            return null;
        }

        public static ImageSource LoadBrand()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                var resource = asm.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith(".Resources.BuildAI.png", StringComparison.OrdinalIgnoreCase));
                if (resource != null)
                {
                    using (var stream = asm.GetManifestResourceStream(resource))
                    {
                        if (stream != null)
                        {
                            var image = Read(stream);
                            BuildAI.Core.Logging.PluginLog.Info("ACC_BRAND_ICON_LOADED source=embedded");
                            return image;
                        }
                    }
                }
            }
            catch (Exception ex) { BuildAI.Core.Logging.PluginLog.Error("ACC_BRAND_ICON_LOAD_FAILED", ex); }
            BuildAI.Core.Logging.PluginLog.Info("ACC_BRAND_ICON_FALLBACK source=buildai32");
            return Load("buildai", 32);
        }

        private static ImageSource Read(Stream stream)
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static PushButton AddButton(RibbonPanel panel, PushButtonData data, string name)
        {
            if (panel == null) throw new ArgumentNullException(nameof(panel));
            if (data == null) throw new ArgumentNullException(nameof(data));

            var button = panel.AddItem(data) as PushButton;
            if (button == null) return null;

            // Assign images after Revit has created the actual PushButton.
            // This is more reliable across Revit 2023-2026 than assigning them
            // only to PushButtonData before RibbonPanel.AddItem().
            button.Image = Load(name, 16);
            button.LargeImage = Load(name, 32);
            return button;
        }
    }
}
