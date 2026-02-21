namespace VideoTrayApp
{
    partial class SettingsForm
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            var lblTitle = new Label();
            var lblFolderPath = new Label();
            txtFolderPath = new TextBox();
            var btnBrowse = new Button();
            var btnSave = new Button();
            var btnCancel = new Button();

            SuspendLayout();

            // Title
            lblTitle.AutoSize = true;
            lblTitle.Font = new Font("Segoe UI", 14F, FontStyle.Bold);
            lblTitle.Location = new Point(20, 20);
            lblTitle.Text = "Video Folder Settings";

            // Folder Path Label
            lblFolderPath.AutoSize = true;
            lblFolderPath.Font = new Font("Segoe UI", 10F);
            lblFolderPath.Location = new Point(20, 70);
            lblFolderPath.Text = "Folder Path:";

            // Text Box
            txtFolderPath.Font = new Font("Segoe UI", 10F);
            txtFolderPath.Location = new Point(20, 95);
            txtFolderPath.Size = new Size(340, 30);
            txtFolderPath.Name = "txtFolderPath";

            // Browse Button
            btnBrowse.Text = "📁 Browse";
            btnBrowse.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnBrowse.Location = new Point(370, 95);
            btnBrowse.Size = new Size(100, 30);
            btnBrowse.BackColor = Color.FromArgb(100, 100, 100);
            btnBrowse.ForeColor = Color.White;
            btnBrowse.FlatStyle = FlatStyle.Flat;
            btnBrowse.FlatAppearance.BorderSize = 0;
            btnBrowse.Cursor = Cursors.Hand;
            btnBrowse.Click += btnBrowse_Click;

            // Save Button
            btnSave.Text = "💾 Save";
            btnSave.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnSave.Location = new Point(280, 150);
            btnSave.Size = new Size(90, 35);
            btnSave.BackColor = Color.FromArgb(0, 176, 240);
            btnSave.ForeColor = Color.White;
            btnSave.FlatStyle = FlatStyle.Flat;
            btnSave.FlatAppearance.BorderSize = 0;
            btnSave.Cursor = Cursors.Hand;
            btnSave.Click += btnSave_Click;

            // Cancel Button
            btnCancel.Text = "✕ Cancel";
            btnCancel.Font = new Font("Segoe UI", 10F, FontStyle.Bold);
            btnCancel.Location = new Point(380, 150);
            btnCancel.Size = new Size(90, 35);
            btnCancel.BackColor = Color.FromArgb(200, 200, 200);
            btnCancel.ForeColor = Color.Black;
            btnCancel.FlatStyle = FlatStyle.Flat;
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Cursor = Cursors.Hand;
            btnCancel.Click += btnCancel_Click;

            // Form
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.White;
            ClientSize = new Size(490, 210);
            Controls.Add(lblTitle);
            Controls.Add(lblFolderPath);
            Controls.Add(txtFolderPath);
            Controls.Add(btnBrowse);
            Controls.Add(btnSave);
            Controls.Add(btnCancel);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Text = "Settings";
            Name = "SettingsForm";

            ResumeLayout(false);
            PerformLayout();
        }

        private TextBox txtFolderPath;
    }
}
