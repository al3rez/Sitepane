namespace Sitepane;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && components is not null)
            components.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1280, 800);
        MinimumSize = new Size(400, 300);
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "Sitepane";
        ResumeLayout(false);
    }
}
