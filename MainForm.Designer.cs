namespace WebView2Browser
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            controlWebView2 = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)controlWebView2).BeginInit();
            SuspendLayout();
            // 
            // controlWebView2
            // 
            controlWebView2.AllowExternalDrop = true;
            controlWebView2.CreationProperties = null;
            controlWebView2.DefaultBackgroundColor = Color.White;
            controlWebView2.Dock = DockStyle.Fill;
            controlWebView2.Location = new Point(0, 0);
            controlWebView2.Name = "controlWebView2";
            controlWebView2.Size = new Size(1262, 673);
            controlWebView2.TabIndex = 0;
            controlWebView2.ZoomFactor = 1D;
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1262, 673);
            Controls.Add(controlWebView2);
            MaximumSize = new Size(1280, 720);
            MinimumSize = new Size(1280, 720);
            Name = "MainForm";
            Text = "WebView2Browser";
            ((System.ComponentModel.ISupportInitialize)controlWebView2).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private Microsoft.Web.WebView2.WinForms.WebView2 controlWebView2;
    }
}
