namespace VideoTrayApp
{
    public partial class SettingsForm : Form
    {
        public string SelectedFolderPath { get; private set; }

        public SettingsForm(string currentFolderPath)
        {
            InitializeComponent();
            SelectedFolderPath = currentFolderPath;
            txtFolderPath.Text = currentFolderPath;
        }

        private void btnBrowse_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Select the folder to monitor for video files"
            };

            if (!string.IsNullOrEmpty(txtFolderPath.Text) && Directory.Exists(txtFolderPath.Text))
            {
                dlg.SelectedPath = txtFolderPath.Text;
            }

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                txtFolderPath.Text = dlg.SelectedPath;
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            string folderPath = txtFolderPath.Text.Trim();

            if (string.IsNullOrEmpty(folderPath))
            {
                MessageBox.Show(
                    "Please enter a folder path or select one using the Browse button.",
                    "Invalid Path",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return;
            }

            if (!Directory.Exists(folderPath))
            {
                MessageBox.Show(
                    $"The folder does not exist:\n{folderPath}",
                    "Folder Not Found",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return;
            }

            SelectedFolderPath = folderPath;
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void btnCancel_Click(object sender, EventArgs e)
        {
            this.DialogResult = DialogResult.Cancel;
            this.Close();
        }
    }
}
