namespace VideoTrayApp.FileOperations
{
    public class NameTenStepsProcessor
    {
        public void Execute(string folder, int start)
        {
            var dir = new DirectoryInfo(folder);
            if (!dir.Exists) throw new DirectoryNotFoundException(folder);
            
            var files = dir.EnumerateFiles()
                .Where(fi => !fi.Name.StartsWith(".") && Form1.DefaultVideoExts.Contains(fi.Extension))
                .ToList();
            
            if (files.Count == 0) return;
            
            files.Sort((a, b) => NaturalSortCompare(a.Name, b.Name));
            // ... rest of implementation
        }
        
        // Move helper methods here
    }
}