using System;
using System.Windows;
using System.Windows.Input;

namespace WindowSpy
{
    public partial class OverlayPickWindow : Window
    {
        public Point ClickPoint { get; private set; }

        public OverlayPickWindow()
        {
            InitializeComponent();
            Cursor = Cursors.Cross;
            NativeMethods.CoverVirtualScreen(this);   // 覆盖全部显示器（含副屏/负坐标）
        }

        private void RootCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Optional: Handle initial click logic if needed
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { DialogResult = false; Close(); }
        }

        private void RootCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
             var pos = e.GetPosition(this);
             var screenPos = PointToScreen(pos);
             ClickPoint = screenPos;
             DialogResult = true;
             Close();
        }
    }
}
