using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace WindowSpy
{
    /// <summary>
    /// 图像模板库（工业视觉「模板标定」产物）：
    /// 模板以 PNG 存在 exe 旁 templates\ 目录，文件名即模板名，流程节点按名引用。
    /// </summary>
    public static class TemplateManager
    {
        public static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "templates");

        public static string PathOf(string name)
            => Path.Combine(Dir, Sanitize(name) + ".png");

        public static bool Exists(string name) => File.Exists(PathOf(name));

        public static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "template" : name.Trim();
        }

        /// <summary>用屏幕矩形截图标定并保存模板，返回文件路径</summary>
        public static string Calibrate(string name, Rectangle screenRect)
        {
            Directory.CreateDirectory(Dir);
            using var bmp = NativeMethods.CaptureScreenRect(screenRect);
            var path = PathOf(name);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            return path;
        }

        public static List<string> List()
        {
            if (!Directory.Exists(Dir)) return new List<string>();
            return Directory.GetFiles(Dir, "*.png")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n)
                .ToList()!;
        }

        public static void Delete(string name)
        {
            try { if (Exists(name)) File.Delete(PathOf(name)); } catch { }
        }
    }
}
