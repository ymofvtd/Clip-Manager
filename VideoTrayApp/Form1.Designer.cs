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
            var pnlHeader = new Panel();
            var pnlContent = new Panel();
            var pnlStats = new Panel();
            var lblTitle = new Label();
            var lblSubtitle = new Label();
            lblTotalTime = new Label();
            var lblTotalTimeLabel = new Label();
            var pnlActions = new Panel();
            btnShuffleAndName = new Button();
            btnNameTenSteps = new Button();
            btnCheck = new Button();
            btnPrepare = new Button();

            SuspendLayout();

            // Header Panel
            pnlHeader.BackColor = Color.FromArgb(45, 45, 48);
            pnlHeader.Dock = DockStyle.Top;
            pnlHeader.Height = 80;
            pnlHeader.Padding = new Padding(20);

            lblTitle.Text = "Video Duration Tracker";
            lblTitle.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
            lblTitle.ForeColor = Color.White;
            lblTitle.AutoSize = true;
            lblTitle.Location = new Point(20, 15);

            lblSubtitle.Text = "Manage and organize your video files";
            lblSubtitle.Font = new Font("Segoe UI", 10F);
            lblSubtitle.ForeColor = Color.FromArgb(200, 200, 200);
            lblSubtitle.AutoSize = true;
            lblSubtitle.Location = new Point(20, 45);

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);

            // Stats Panel
            pnlStats.BackColor = Color.FromArgb(37, 37, 40);
            pnlStats.Dock = DockStyle.Top;
            pnlStats.Height = 120;
            pnlStats.Padding = new Padding(20);

            lblTotalTimeLabel.Text = "Total Duration";
            lblTotalTimeLabel.Font = new Font("Segoe UI", 10F);
            lblTotalTimeLabel.ForeColor = Color.FromArgb(150, 150, 150);
            lblTotalTimeLabel.Location = new Point(20, 20);
            lblTotalTimeLabel.AutoSize = true;

            lblTotalTime.Text = "Calculating...";
            lblTotalTime.Font = new Font("Segoe UI", 42F, FontStyle.Bold);
            lblTotalTime.ForeColor = Color.FromArgb(0, 176, 240);
            lblTotalTime.Location = new Point(20, 45);
            lblTotalTime.AutoSize = true;
            lblTotalTime.Click += label1_Click;

            pnlStats.Controls.Add(lblTotalTimeLabel);
            pnlStats.Controls.Add(lblTotalTime);

            // Actions Panel
            pnlActions.BackColor = Color.FromArgb(45, 45, 48);
            pnlActions.Dock = DockStyle.Fill;
            pnlActions.Padding = new Padding(20);

            // Shuffle and Name Button
            btnShuffleAndName.Text = "🔀 Shuffle && Name";
            btnShuffleAndName.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnShuffleAndName.BackColor = Color.FromArgb(0, 176, 240);
            btnShuffleAndName.ForeColor = Color.White;
            btnShuffleAndName.Size = new Size(200, 50);
            btnShuffleAndName.Location = new Point(20, 20);
            btnShuffleAndName.TabIndex = 1;
            btnShuffleAndName.FlatStyle = FlatStyle.Flat;
            btnShuffleAndName.FlatAppearance.BorderSize = 0;
            btnShuffleAndName.Cursor = Cursors.Hand;
            btnShuffleAndName.Click += btnShuffleAndName_Click;

            // Name by 10s Button
            btnNameTenSteps.Text = "📝 Name by 10s";
            btnNameTenSteps.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnNameTenSteps.BackColor = Color.FromArgb(100, 100, 100);
            btnNameTenSteps.ForeColor = Color.White;
            btnNameTenSteps.Size = new Size(200, 50);
            btnNameTenSteps.Location = new Point(230, 20);
            btnNameTenSteps.TabIndex = 2;
            btnNameTenSteps.FlatStyle = FlatStyle.Flat;
            btnNameTenSteps.FlatAppearance.BorderSize = 0;
            btnNameTenSteps.Cursor = Cursors.Hand;
            btnNameTenSteps.Click += btnNameTenSteps_Click;

            // Check Button
            btnCheck.Text = "✓ Check";
            btnCheck.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnCheck.BackColor = Color.FromArgb(76, 175, 80);
            btnCheck.ForeColor = Color.White;
            btnCheck.Size = new Size(150, 50);
            btnCheck.Location = new Point(440, 20);
            btnCheck.TabIndex = 3;
            btnCheck.FlatStyle = FlatStyle.Flat;
            btnCheck.FlatAppearance.BorderSize = 0;
            btnCheck.Cursor = Cursors.Hand;
            btnCheck.Click += btnCheck_Click;

            // Prepare Batch Button
            btnPrepare.Text = "⚙️ Prepare Batch";
            btnPrepare.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            btnPrepare.BackColor = Color.FromArgb(156, 39, 176);
            btnPrepare.ForeColor = Color.White;
            btnPrepare.Size = new Size(150, 50);
            btnPrepare.Location = new Point(600, 20);
            btnPrepare.TabIndex = 4;
            btnPrepare.FlatStyle = FlatStyle.Flat;
            btnPrepare.FlatAppearance.BorderSize = 0;
            btnPrepare.Cursor = Cursors.Hand;
            btnPrepare.Click += btnPrepare_Click;

            pnlActions.Controls.Add(btnShuffleAndName);
            pnlActions.Controls.Add(btnNameTenSteps);
            pnlActions.Controls.Add(btnCheck);
            pnlActions.Controls.Add(btnPrepare);

            // Main Content Panel
            pnlContent.BackColor = Color.FromArgb(45, 45, 48);
            pnlContent.Dock = DockStyle.Fill;
            pnlContent.Controls.Add(pnlActions);

            // Form1
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(900, 400);
            BackColor = Color.FromArgb(45, 45, 48);
            ForeColor = Color.White;
            Controls.Add(pnlContent);
            Controls.Add(pnlStats);
            Controls.Add(pnlHeader);
            Name = "Form1";
            Text = "Video Duration Tracker";
            StartPosition = FormStartPosition.CenterScreen;
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblTotalTime;
        private Button btnShuffleAndName;
        private Button btnNameTenSteps;
        private Button btnCheck;
        private Button btnPrepare;
    }
}
