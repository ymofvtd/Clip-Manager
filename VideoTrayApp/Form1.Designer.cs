namespace VideoTrayApp
{
    partial class Form1
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
            components = new System.ComponentModel.Container();
            var pnlTop = new Panel();
            var pnlFolder = new Panel();
            var pnlContent = new Panel();
            var pnlActions = new Panel();
            var lblFolderLabel = new Label();
            btnPrepare = new Button();
            btnShuffleAndName = new Button();
            btnNameTenSteps = new Button();
            btnShuffleRandom = new Button();
            btnArchive = new Button();
            btnBrowseFolder = new Button();
            txtSelectedFolder = new TextBox();
            lblArchiveHint = new Label();
            toolTips = new ToolTip(components);

            SuspendLayout();

            // Top Panel - Prepare Batch (main action)
            pnlTop.BackColor = Color.FromArgb(37, 37, 40);
            pnlTop.Dock = DockStyle.Top;
            pnlTop.Height = 64;
            pnlTop.Padding = new Padding(20, 10, 20, 10);

            btnPrepare.Text = "⚙️";
            btnPrepare.Font = new Font("Segoe UI", 22F);
            btnPrepare.BackColor = Color.FromArgb(156, 39, 176);
            btnPrepare.ForeColor = Color.White;
            btnPrepare.Size = new Size(48, 48);
            btnPrepare.Location = new Point(20, 8);
            btnPrepare.TabIndex = 0;
            btnPrepare.FlatStyle = FlatStyle.Flat;
            btnPrepare.FlatAppearance.BorderSize = 0;
            btnPrepare.Cursor = Cursors.Hand;
            btnPrepare.Click += btnPrepare_Click;
            toolTips.SetToolTip(btnPrepare, "Prepare Batch");

            pnlTop.Controls.Add(btnPrepare);

            // Folder Panel
            pnlFolder.BackColor = Color.FromArgb(37, 37, 40);
            pnlFolder.Dock = DockStyle.Top;
            pnlFolder.Height = 96;
            pnlFolder.Padding = new Padding(20);

            lblFolderLabel.Text = "Working Folder";
            lblFolderLabel.Font = new Font("Segoe UI", 10F);
            lblFolderLabel.ForeColor = Color.FromArgb(150, 150, 150);
            lblFolderLabel.Location = new Point(20, 16);
            lblFolderLabel.AutoSize = true;

            txtSelectedFolder.Font = new Font("Segoe UI", 10F);
            txtSelectedFolder.BackColor = Color.FromArgb(30, 30, 30);
            txtSelectedFolder.ForeColor = Color.White;
            txtSelectedFolder.BorderStyle = BorderStyle.FixedSingle;
            txtSelectedFolder.ReadOnly = true;
            txtSelectedFolder.Location = new Point(20, 40);
            txtSelectedFolder.Size = new Size(300, 27);
            txtSelectedFolder.TabIndex = 1;

            btnBrowseFolder.Text = "Browse...";
            btnBrowseFolder.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnBrowseFolder.BackColor = Color.FromArgb(100, 100, 100);
            btnBrowseFolder.ForeColor = Color.White;
            btnBrowseFolder.Size = new Size(90, 27);
            btnBrowseFolder.Location = new Point(330, 40);
            btnBrowseFolder.TabIndex = 2;
            btnBrowseFolder.FlatStyle = FlatStyle.Flat;
            btnBrowseFolder.FlatAppearance.BorderSize = 0;
            btnBrowseFolder.Cursor = Cursors.Hand;
            btnBrowseFolder.Click += btnBrowseFolder_Click;

            pnlFolder.Controls.Add(lblFolderLabel);
            pnlFolder.Controls.Add(txtSelectedFolder);
            pnlFolder.Controls.Add(btnBrowseFolder);

            // Actions Panel
            pnlActions.BackColor = Color.FromArgb(45, 45, 48);
            pnlActions.Dock = DockStyle.Fill;
            pnlActions.Padding = new Padding(20);

            btnShuffleAndName.Text = "🔀 Shuffle & Name";
            btnShuffleAndName.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnShuffleAndName.BackColor = Color.FromArgb(0, 176, 240);
            btnShuffleAndName.ForeColor = Color.White;
            btnShuffleAndName.Size = new Size(180, 34);
            btnShuffleAndName.Location = new Point(20, 16);
            btnShuffleAndName.TabIndex = 3;
            btnShuffleAndName.FlatStyle = FlatStyle.Flat;
            btnShuffleAndName.FlatAppearance.BorderSize = 0;
            btnShuffleAndName.Cursor = Cursors.Hand;
            btnShuffleAndName.Click += btnShuffleAndName_Click;

            btnNameTenSteps.Text = "📝 Name by 10s";
            btnNameTenSteps.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnNameTenSteps.BackColor = Color.FromArgb(100, 100, 100);
            btnNameTenSteps.ForeColor = Color.White;
            btnNameTenSteps.Size = new Size(180, 34);
            btnNameTenSteps.Location = new Point(20, 58);
            btnNameTenSteps.TabIndex = 4;
            btnNameTenSteps.FlatStyle = FlatStyle.Flat;
            btnNameTenSteps.FlatAppearance.BorderSize = 0;
            btnNameTenSteps.Cursor = Cursors.Hand;
            btnNameTenSteps.Click += btnNameTenSteps_Click;

            btnShuffleRandom.Text = "🎲 Shuffle Random";
            btnShuffleRandom.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnShuffleRandom.BackColor = Color.FromArgb(255, 152, 0);
            btnShuffleRandom.ForeColor = Color.White;
            btnShuffleRandom.Size = new Size(180, 34);
            btnShuffleRandom.Location = new Point(20, 100);
            btnShuffleRandom.TabIndex = 5;
            btnShuffleRandom.FlatStyle = FlatStyle.Flat;
            btnShuffleRandom.FlatAppearance.BorderSize = 0;
            btnShuffleRandom.Cursor = Cursors.Hand;
            btnShuffleRandom.Click += btnShuffleRandom_Click;

            lblArchiveHint.Text = "Click here to archive";
            lblArchiveHint.Font = new Font("Segoe UI", 8F);
            lblArchiveHint.ForeColor = Color.FromArgb(150, 150, 150);
            lblArchiveHint.AutoSize = true;
            lblArchiveHint.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            lblArchiveHint.Location = new Point(20, 280);

            btnArchive.Text = "📦 Archive";
            btnArchive.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
            btnArchive.BackColor = Color.FromArgb(63, 81, 181);
            btnArchive.ForeColor = Color.White;
            btnArchive.Size = new Size(140, 34);
            btnArchive.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            btnArchive.Location = new Point(20, 302);
            btnArchive.TabIndex = 6;
            btnArchive.FlatStyle = FlatStyle.Flat;
            btnArchive.FlatAppearance.BorderSize = 0;
            btnArchive.Cursor = Cursors.Hand;
            btnArchive.Click += btnArchive_Click;

            pnlActions.Controls.Add(btnShuffleAndName);
            pnlActions.Controls.Add(btnNameTenSteps);
            pnlActions.Controls.Add(btnShuffleRandom);
            pnlActions.Controls.Add(lblArchiveHint);
            pnlActions.Controls.Add(btnArchive);

            // Main Content Panel
            pnlContent.BackColor = Color.FromArgb(45, 45, 48);
            pnlContent.Dock = DockStyle.Fill;
            pnlContent.Controls.Add(pnlActions);

            // Form1
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(440, 520);
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.White;
            MinimumSize = new Size(440, 520);
            Controls.Add(pnlContent);
            Controls.Add(pnlFolder);
            Controls.Add(pnlTop);
            Name = "Form1";
            Text = "Video Tray";
            StartPosition = FormStartPosition.CenterScreen;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Button btnPrepare;
        private Button btnShuffleAndName;
        private Button btnNameTenSteps;
        private Button btnShuffleRandom;
        private Button btnArchive;
        private Button btnBrowseFolder;
        private TextBox txtSelectedFolder;
        private Label lblArchiveHint;
        private ToolTip toolTips;
    }
}