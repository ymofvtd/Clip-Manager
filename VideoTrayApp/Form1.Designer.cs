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
            lblTotalTime = new Label();
            btnShuffleAndName = new Button();
            btnNameTenSteps = new Button();
            btnCheck = new Button();
            SuspendLayout();
            // 
            // lblTotalTime
            // 
            lblTotalTime.AutoSize = true;
            lblTotalTime.Font = new Font("Segoe UI", 24F);
            lblTotalTime.Location = new Point(919, 490);
            lblTotalTime.Name = "lblTotalTime";
            lblTotalTime.Size = new Size(198, 45);
            lblTotalTime.TabIndex = 0;
            lblTotalTime.Text = "Calculating...";
            lblTotalTime.Click += label1_Click;
            // 
            // btnShuffleAndName
            // 
            btnShuffleAndName.Location = new Point(12, 12);
            btnShuffleAndName.Name = "btnShuffleAndName";
            btnShuffleAndName.Size = new Size(180, 30);
            btnShuffleAndName.TabIndex = 1;
            btnShuffleAndName.Text = "Shuffle && Name (Step 10)";
            btnShuffleAndName.UseVisualStyleBackColor = true;
            btnShuffleAndName.Click += btnShuffleAndName_Click;
            // 
            // btnNameTenSteps
            // 
            btnNameTenSteps.Location = new Point(198, 12);
            btnNameTenSteps.Name = "btnNameTenSteps";
            btnNameTenSteps.Size = new Size(180, 30);
            btnNameTenSteps.TabIndex = 2;
            btnNameTenSteps.Text = "Name by 10s (Step 10)";
            btnNameTenSteps.UseVisualStyleBackColor = true;
            btnNameTenSteps.Click += btnNameTenSteps_Click;
            // 
            // btnCheck
            // 
            btnCheck.Location = new Point(384, 12);
            btnCheck.Name = "btnCheck";
            btnCheck.Size = new Size(80, 30);
            btnCheck.TabIndex = 3;
            btnCheck.Text = "Check";
            btnCheck.UseVisualStyleBackColor = true;
            btnCheck.Click += btnCheck_Click;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(1129, 544);
            Controls.Add(btnCheck);
            Controls.Add(btnNameTenSteps);
            Controls.Add(btnShuffleAndName);
            Controls.Add(lblTotalTime);
            Name = "Form1";
            Text = "Form1";
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Label lblTotalTime;
        private Button btnShuffleAndName;
        private Button btnNameTenSteps;
        private Button btnCheck;
    }
}
