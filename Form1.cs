using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace Parser_v2
{
    public partial class Form1 : Form
    {
        private static readonly string RootDir = Path.Combine(Application.StartupPath, "img");
        private static readonly HttpClient client = new HttpClient();
        private static readonly int MaxConcurrentDownloads = 10;
        private int totalPages;
        private int downloadedFilesCount;
        private int expectedFilesCount;
        private Random random = new Random();
        private PictureBox currentlyHighlightedPictureBox;

        public Form1()
        {
            InitializeComponent();
            nudPages.Enabled = false;
            btnDownload.Enabled = false;
        }

        private async void btnCheckPages_Click(object sender, EventArgs e)
        {
            string tag = txtTag.Text.Trim();
            if (string.IsNullOrEmpty(tag))
            {
                MessageBox.Show("Please enter a tag.", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnCheckPages.Enabled = false;
            lblStatus.Text = "Loading total pages...";
            totalPages = await GetTotalPosts(tag);
            int postsPerPage = 42;
            totalPages = (totalPages + postsPerPage - 1) / postsPerPage;

            lblStatus.Text = $"Total number of pages for the tag '{tag}': {totalPages}";
            nudPages.Maximum = totalPages;
            nudPages.Value = totalPages;
            nudPages.Enabled = true;
            btnDownload.Enabled = true;
            btnCheckPages.Enabled = true;
        }

        private async void btnDownload_Click(object sender, EventArgs e)
        {
            string tag = txtTag.Text.Trim();
            if (string.IsNullOrEmpty(tag))
            {
                MessageBox.Show("Please enter a tag.", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!int.TryParse(nudPages.Value.ToString(), out int maxPages) || maxPages > totalPages)
            {
                MessageBox.Show("Please enter a valid number of pages.", "Input Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnDownload.Enabled = false;
            progressBar.Value = 0;
            lblStatus.Text = "Loading URLs...";
            flowLayoutPanel.Controls.Clear();

            List<string[]> allUrls = new List<string[]>();
            for (int page = 0; page < maxPages; page++)
            {
                lblStatus.Text = $"Processing page {page + 1}/{totalPages}";
                var listUrl = await ListTag(page, tag);
                allUrls.AddRange(listUrl);
            }

            string tagDir = GetUniqueFolder(RootDir, tag);

            lblStatus.Text = "Downloading files...";
            HashSet<string> downloadedFiles = new HashSet<string>();
            await DownloadFiles(allUrls, tagDir, downloadedFiles);

            expectedFilesCount = allUrls.Count;
            downloadedFilesCount = downloadedFiles.Count;

            if (downloadedFilesCount < expectedFilesCount)
            {
                lblStatus.Text = $"Missing {expectedFilesCount - downloadedFilesCount} files. Attempting to redownload...";

                List<string[]> missingUrls = allUrls.FindAll(url => !downloadedFiles.Contains(url[1]));
                await DownloadFiles(missingUrls, tagDir, downloadedFiles);

                downloadedFilesCount = downloadedFiles.Count;
            }

            lblStatus.Text = $"{downloadedFilesCount} files downloaded to {tagDir}";
            btnDownload.Enabled = true;
        }

        private async Task<int> GetTotalPosts(string tag)
        {
            string tags = Uri.EscapeDataString(tag);
            string url = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&limit=1&tags={tags}";
            HttpResponseMessage response = await SendRequestWithRetry(url);
            string responseContent = await response.Content.ReadAsStringAsync();
            XDocument xmlDoc = XDocument.Parse(responseContent);
            if (xmlDoc.Root?.Attribute("count") is { } countAttr)
            {
                return int.Parse(countAttr.Value);
            }
            else
            {
                throw new Exception("Could not find 'count' attribute in the response.");
            }
        }

        private async Task<List<string[]>> ListTag(int page, string tag)
        {
            string tags = Uri.EscapeDataString(tag);
            string url = $"https://rule34.xxx/index.php?page=dapi&s=post&q=index&pid={page}&tags={tags}";
            HttpResponseMessage response = await SendRequestWithRetry(url);
            string responseContent = await response.Content.ReadAsStringAsync();
            XDocument xmlDoc = XDocument.Parse(responseContent);
            List<string[]> returnData = new List<string[]>();

            foreach (var post in xmlDoc.Descendants("post"))
            {
                XAttribute fileUrlAttr = post.Attribute("file_url");
                if (fileUrlAttr is { } fileUrl && IsValidImageExtension(fileUrl.Value))
                {
                    XAttribute tagsAttr = post.Attribute("tags");
                    string tagsValue = tagsAttr?.Value ?? string.Empty;
                    returnData.Add(new string[] { tagsValue, fileUrl.Value });
                }
            }

            return returnData;
        }

        private bool IsValidImageExtension(string url)
        {
            string extension = Path.GetExtension(url)?.ToLower() ?? string.Empty;
            return extension switch
            {
                ".jpg" => true,
                ".jpeg" => true,
                ".png" => true,
                ".gif" => true,
                ".bmp" => true,
                _ => false,
            };
        }

        private string GetUniqueFolder(string baseDir, string tag)
        {
            string basePath = Path.Combine(baseDir, tag);
            int counter = 0;
            while (true)
            {
                string path;
                if (counter == 0)
                {
                    path = basePath;
                }
                else
                {
                    path = $"{basePath}({counter})";
                }
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                    return path;
                }
                counter++;
            }
        }

        private async Task DownloadFiles(List<string[]> urls, string tagDir, HashSet<string> downloadedFiles)
        {
            List<Task> downloadTasks = new List<Task>();
            SemaphoreSlim semaphore = new SemaphoreSlim(MaxConcurrentDownloads);

            foreach (var oneUrl in urls)
            {
                await semaphore.WaitAsync();
                downloadTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await LoadUrl(oneUrl[1], tagDir, downloadedFiles);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(downloadTasks);
        }

        private async Task LoadUrl(string url, string tagDir, HashSet<string> downloadedFiles)
        {
            try
            {
                string filename = Path.Combine(tagDir, Path.GetFileName(url));
                if (File.Exists(filename))
                {
                    lblStatus.Invoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = $"File {filename} already exists. Skipping.";
                    });
                    downloadedFiles.Add(url);
                    return;
                }

                HttpResponseMessage response = await SendRequestWithRetry(url);
                response.EnsureSuccessStatusCode();
                byte[] content = await response.Content.ReadAsByteArrayAsync();

                using (FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await fs.WriteAsync(content, 0, content.Length);
                }

                downloadedFiles.Add(url);

                lblStatus.Invoke((MethodInvoker)delegate
                {
                    lblStatus.Text = $"Downloaded {filename}";
                    downloadedFilesCount++;
                    int progressValue = (int)((downloadedFilesCount / (float)expectedFilesCount) * 100);
                    progressBar.Value = Clamp(progressValue, 0, 100); // Use custom Clamp method
                    AddImageToPanel(filename);
                });
            }
            catch (HttpRequestException ex)
            {
                lblStatus.Invoke((MethodInvoker)delegate
                {
                    lblStatus.Text = $"HTTP Error: {ex.Message}";
                });
            }
            catch (Exception ex)
            {
                lblStatus.Invoke((MethodInvoker)delegate
                {
                    lblStatus.Text = $"Error: {ex.Message}";
                });
            }
        }

        private async Task<HttpResponseMessage> SendRequestWithRetry(string url)
        {
            int retryCount = 0;
            while (retryCount < 5)
            {
                try
                {
                    HttpResponseMessage response = await client.GetAsync(url);
                    response.EnsureSuccessStatusCode();
                    return response;
                }
                catch (WebException ex) when (((HttpWebResponse)ex.Response)?.StatusCode == (HttpStatusCode)429)
                {
                    retryCount++;
                    int delay = 1000 * (int)Math.Pow(2, retryCount - 1);
                    delay += random.Next(0, 500);
                    lblStatus.Invoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = $"429 Too Many Requests. Retrying in {delay} ms...";
                    });
                    await Task.Delay(delay);
                }
                catch (HttpRequestException ex)
                {
                    lblStatus.Invoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = $"HTTP Error: {ex.Message}";
                    });
                    throw;
                }
                catch (Exception ex)
                {
                    lblStatus.Invoke((MethodInvoker)delegate
                    {
                        lblStatus.Text = $"Error: {ex.Message}";
                    });
                    throw;
                }
            }
            throw new HttpRequestException("Exceeded maximum number of retries for 429 Too Many Requests.");
        }

        private void AddImageToPanel(string filePath)
        {
            PictureBox pictureBox = new PictureBox
            {
                ImageLocation = filePath,
                SizeMode = PictureBoxSizeMode.Zoom,
                Size = new Size(100, 100),
                Margin = new Padding(5),
                BorderStyle = BorderStyle.FixedSingle
            };

            pictureBox.Click += (sender, e) => ToggleImageSelection(sender as PictureBox, filePath);

            flowLayoutPanel.Controls.Add(pictureBox);
        }

        private void ToggleImageSelection(PictureBox pictureBox, string filePath)
        {
            if (currentlyHighlightedPictureBox == pictureBox)
            {
                OpenImage(filePath);
                currentlyHighlightedPictureBox.BackColor = Color.Transparent;
                currentlyHighlightedPictureBox = null;
            }
            else
            {
                if (currentlyHighlightedPictureBox != null)
                {
                    currentlyHighlightedPictureBox.BackColor = Color.Transparent;
                }
                pictureBox.BackColor = Color.Blue;
                currentlyHighlightedPictureBox = pictureBox;
            }
        }

        private void OpenImage(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open image: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private int Clamp(int value, int min, int max)
        {
            return Math.Min(Math.Max(value, min), max);
        }
    }
}