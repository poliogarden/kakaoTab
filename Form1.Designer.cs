namespace KakaoTab;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;
    private System.Windows.Forms.TabControl tabControlMain;
    private System.Windows.Forms.Panel topPanel;

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
        this.components = new System.ComponentModel.Container();
        this.topPanel = new System.Windows.Forms.Panel();
        this.tabControlMain = new System.Windows.Forms.TabControl();
        this.SuspendLayout();
        // 
        // topPanel
        // 
        this.topPanel.Dock = System.Windows.Forms.DockStyle.Top;
        this.topPanel.Height = 32;
        this.topPanel.Name = "topPanel";
        this.topPanel.TabIndex = 1;
        // 
        // tabControlMain
        // 
        this.tabControlMain.Dock = System.Windows.Forms.DockStyle.Fill;
        this.tabControlMain.Location = new System.Drawing.Point(0, 32);
        this.tabControlMain.Name = "tabControlMain";
        this.tabControlMain.SelectedIndex = 0;
        this.tabControlMain.Size = new System.Drawing.Size(800, 418);
        this.tabControlMain.TabIndex = 0;
        // 
        // Form1
        // 
        this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
        this.ClientSize = new System.Drawing.Size(800, 450);
        this.Controls.Add(this.tabControlMain);
        this.Controls.Add(this.topPanel);
        this.Name = "Form1";
        this.Text = "KakaoTab";
        this.ResumeLayout(false);
    }

    #endregion
}
