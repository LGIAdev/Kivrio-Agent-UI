using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Forms;

namespace KivrioAgentUiInstaller
{
    internal static class Program
    {
        private static readonly byte[] PayloadMagic = new byte[] { 0x4B, 0x49, 0x56, 0x50, 0x41, 0x59, 0x4C, 0x31 };
        private const int FooterSize = 16;

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            InstallProgressForm progress = null;
            string installDir = null;
            string backupInstallDir = null;
            try
            {
                progress = new InstallProgressForm();
                progress.Show();
                progress.Activate();
                progress.SetPreparingFiles();

                string exePath = Application.ExecutablePath;
                string extractRoot = Path.Combine(Path.GetTempPath(), "KV");
                string zipPath = Path.Combine(extractRoot, "kivrio-agent-ui-package.zip");
                installDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kivrio Agent UI");
                backupInstallDir = installDir + ".previous";
                string startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Kivrio Agent UI"
                );

                TryDeleteDirectory(extractRoot);
                Directory.CreateDirectory(extractRoot);
                Directory.CreateDirectory(startMenuDir);

                ExtractPayload(exePath, zipPath);
                ZipFile.ExtractToDirectory(zipPath, extractRoot);

                string packageRoot = Path.Combine(extractRoot, "app");
                if (!Directory.Exists(packageRoot))
                {
                    throw new InvalidOperationException("Le package Kivrio Agent UI est invalide: dossier app introuvable.");
                }

                TryDeleteDirectory(backupInstallDir);
                if (Directory.Exists(installDir))
                {
                    Directory.Move(installDir, backupInstallDir);
                }

                Directory.CreateDirectory(installDir);
                CopyDirectory(packageRoot, installDir);
                RestoreUserData(backupInstallDir, installDir);
                Directory.CreateDirectory(Path.Combine(installDir, "data"));
                Directory.CreateDirectory(Path.Combine(installDir, "data", "uploads"));

                string iconPath = Path.Combine(installDir, "assets", "kivrio-agent-ui.ico");
                if (!File.Exists(iconPath))
                {
                    throw new InvalidOperationException("Icone Kivrio Agent UI introuvable apres installation.");
                }

                string desktopShortcut = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    "Kivrio Agent UI.lnk"
                );
                string startMenuShortcut = Path.Combine(startMenuDir, "Kivrio Agent UI.lnk");
                CreateShortcut(desktopShortcut, installDir, iconPath);
                CreateShortcut(startMenuShortcut, installDir, iconPath);

                progress.SetLaunching();

                string launcher = Path.Combine(installDir, "start-kivrio-agent-ui-hidden.vbs");
                Process.Start(
                    new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.SystemDirectory, "wscript.exe"),
                        Arguments = "\"" + launcher + "\"",
                        UseShellExecute = true,
                        WorkingDirectory = installDir,
                    }
                );

                TryDeleteDirectory(backupInstallDir);
                TryDeleteDirectory(extractRoot);
                progress.Close();
                progress.Dispose();
                progress = null;

                MessageBox.Show(
                    "Kivrio Agent UI a ete installe dans AppData\\Local\\Kivrio Agent UI et un raccourci Bureau a ete cree.",
                    "Installation Kivrio Agent UI",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return 0;
            }
            catch (Exception ex)
            {
                if (progress != null)
                {
                    try
                    {
                        progress.Close();
                        progress.Dispose();
                    }
                    catch
                    {
                    }
                }

                TryRestorePreviousInstallation(installDir, backupInstallDir);

                string errorPath = Path.Combine(Path.GetTempPath(), "kivrio-agent-ui-installer-error.txt");
                try
                {
                    File.WriteAllText(errorPath, ex.ToString());
                }
                catch
                {
                }

                MessageBox.Show(
                    ex.Message + Environment.NewLine + Environment.NewLine + "Details: " + errorPath,
                    "Installation Kivrio Agent UI impossible",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
                return 1;
            }
        }

        private static void RestoreUserData(string backupInstallDir, string installDir)
        {
            if (string.IsNullOrEmpty(backupInstallDir) || !Directory.Exists(backupInstallDir))
            {
                return;
            }

            string backupDataDir = Path.Combine(backupInstallDir, "data");
            if (!Directory.Exists(backupDataDir))
            {
                return;
            }

            CopyDirectory(backupDataDir, Path.Combine(installDir, "data"));
        }

        private static void ExtractPayload(string exePath, string destinationZip)
        {
            using (var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= FooterSize)
                {
                    throw new InvalidOperationException("Payload Kivrio Agent UI introuvable dans l'installeur.");
                }

                stream.Seek(-FooterSize, SeekOrigin.End);
                byte[] footer = new byte[FooterSize];
                stream.Read(footer, 0, footer.Length);

                byte[] magic = footer.Take(8).ToArray();
                if (!magic.SequenceEqual(PayloadMagic))
                {
                    throw new InvalidOperationException("Signature du package Kivrio Agent UI invalide.");
                }

                long payloadLength = BitConverter.ToInt64(footer, 8);
                long payloadStart = stream.Length - FooterSize - payloadLength;
                if (payloadLength <= 0 || payloadStart < 0)
                {
                    throw new InvalidOperationException("Longueur du package Kivrio Agent UI invalide.");
                }

                stream.Seek(payloadStart, SeekOrigin.Begin);
                using (var output = new FileStream(destinationZip, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[4 * 1024 * 1024];
                    long remaining = payloadLength;
                    while (remaining > 0)
                    {
                        int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read <= 0)
                        {
                            throw new EndOfStreamException("Lecture incomplete du package Kivrio Agent UI.");
                        }

                        output.Write(buffer, 0, read);
                        remaining -= read;
                    }
                }
            }
        }

        private static void CopyDirectory(string sourceDir, string destinationDir)
        {
            foreach (string directory in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = directory.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                Directory.CreateDirectory(Path.Combine(destinationDir, relative));
            }

            foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(sourceDir.Length).TrimStart(Path.DirectorySeparatorChar);
                string destination = Path.Combine(destinationDir, relative);
                string parent = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                File.Copy(file, destination, true);
            }
        }

        private static void TryRestorePreviousInstallation(string installDir, string backupInstallDir)
        {
            try
            {
                if (string.IsNullOrEmpty(installDir) || string.IsNullOrEmpty(backupInstallDir))
                {
                    return;
                }

                if (!Directory.Exists(backupInstallDir))
                {
                    return;
                }

                TryDeleteDirectory(installDir);
                Directory.Move(backupInstallDir, installDir);
            }
            catch
            {
            }
        }

        private static void CreateShortcut(string shortcutPath, string installDir, string iconPath)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = Path.Combine(Environment.SystemDirectory, "wscript.exe");
            shortcut.Arguments = "\"" + Path.Combine(installDir, "start-kivrio-agent-ui-hidden.vbs") + "\"";
            shortcut.WorkingDirectory = installDir;
            shortcut.IconLocation = iconPath;
            shortcut.Save();
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        private sealed class InstallProgressForm : Form
        {
            private readonly Label _statusValue;
            private readonly StepRow _validationStep;
            private readonly StepRow _copyStep;
            private readonly StepRow _launchStep;

            public InstallProgressForm()
            {
                Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
                Text = "Installation de Kivrio Agent UI";
                StartPosition = FormStartPosition.CenterScreen;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = true;
                ClientSize = new Size(620, 520);
                BackColor = Color.FromArgb(244, 245, 247);

                try
                {
                    Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                }
                catch
                {
                }

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    Padding = new Padding(28, 24, 28, 24),
                    BackColor = Color.FromArgb(244, 245, 247),
                };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                Controls.Add(root);

                root.Controls.Add(BuildHeader(), 0, 0);

                var statusCard = BuildCard();
                statusCard.Padding = new Padding(18);
                statusCard.Margin = new Padding(0, 0, 0, 18);
                var statusLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 3,
                    BackColor = Color.Transparent,
                };
                statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                statusCard.Controls.Add(statusLayout);

                var statusTop = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    ColumnCount = 2,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 0, 0, 12),
                };
                statusTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                statusTop.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                statusLayout.Controls.Add(statusTop);

                var statusLabel = new Label
                {
                    AutoSize = true,
                    Text = "Etat actuel",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(93, 93, 93),
                    Margin = new Padding(0, 5, 0, 0),
                };
                statusTop.Controls.Add(statusLabel, 0, 0);

                _statusValue = new Label
                {
                    AutoSize = true,
                    Text = "Installation en cours...",
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(10, 100, 216),
                    BackColor = Color.FromArgb(238, 246, 255),
                    Padding = new Padding(12, 8, 12, 8),
                    Margin = new Padding(12, 0, 0, 0),
                };
                ApplyRoundedRegion(_statusValue, 16);
                statusTop.Controls.Add(_statusValue, 1, 0);

                var progress = new ProgressBar
                {
                    Dock = DockStyle.Top,
                    Height = 18,
                    Style = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 20,
                    Margin = new Padding(0, 0, 0, 12),
                };
                statusLayout.Controls.Add(progress);

                var statusHelp = new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    Text = "Cette premiere installation peut prendre entre 30 et 90 secondes selon votre machine.",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(93, 93, 93),
                    Margin = new Padding(0),
                };
                statusLayout.Controls.Add(statusHelp);
                root.Controls.Add(statusCard, 0, 1);

                var stepsCard = BuildCard();
                stepsCard.Padding = new Padding(18);
                stepsCard.Margin = new Padding(0, 0, 0, 18);
                var stepsLayout = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 3,
                    BackColor = Color.Transparent,
                };
                stepsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stepsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stepsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                stepsCard.Controls.Add(stepsLayout);

                _validationStep = new StepRow(
                    "Validation de l'installateur",
                    "Le programme d'installation Kivrio Agent UI a ete autorise et demarre."
                );
                _copyStep = new StepRow(
                    "Copie des fichiers Kivrio Agent UI",
                    "Les composants locaux de Kivrio Agent UI sont en cours de preparation."
                );
                _launchStep = new StepRow(
                    "Lancement de Kivrio Agent UI",
                    "Kivrio Agent UI s'ouvrira automatiquement des que l'installation sera prete."
                );

                stepsLayout.Controls.Add(_validationStep);
                stepsLayout.Controls.Add(_copyStep);
                stepsLayout.Controls.Add(_launchStep);
                root.Controls.Add(stepsCard, 0, 2);

                var notice = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 78,
                    BackColor = Color.FromArgb(255, 253, 242),
                    Margin = new Padding(0, 0, 0, 16),
                    Padding = new Padding(16, 14, 16, 14),
                };
                ApplyRoundedRegion(notice, 12);
                var noticeIcon = new Label
                {
                    AutoSize = false,
                    Size = new Size(24, 24),
                    Location = new Point(0, 2),
                    Text = "i",
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 10F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(107, 90, 24),
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                };
                MakeCircular(noticeIcon);
                notice.Controls.Add(noticeIcon);

                var noticeText = new Label
                {
                    AutoSize = false,
                    Location = new Point(38, 0),
                    Size = new Size(500, 48),
                    Text = "Ne fermez pas cette fenetre. Si rien ne s'affiche pendant quelques secondes, cela signifie simplement que Windows termine l'installation en arriere-plan.",
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(107, 90, 24),
                };
                notice.Controls.Add(noticeText);
                root.Controls.Add(notice, 0, 3);

                var footer = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    FlowDirection = FlowDirection.RightToLeft,
                    WrapContents = false,
                    Height = 42,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                    Padding = new Padding(0),
                };
                var button = new Button
                {
                    Text = "Patientez...",
                    Enabled = false,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Padding = new Padding(14, 6, 14, 6),
                    Margin = new Padding(0),
                };
                footer.Controls.Add(button);
                root.Controls.Add(footer);

                SetPreparingFiles();
            }

            public void SetPreparingFiles()
            {
                _statusValue.Text = "Installation en cours...";
                _validationStep.SetState(StepState.Done, "OK");
                _copyStep.SetState(StepState.Active, "2");
                _launchStep.SetState(StepState.Pending, "3");
                RefreshUi();
            }

            public void SetLaunching()
            {
                _statusValue.Text = "Lancement de Kivrio Agent UI...";
                _validationStep.SetState(StepState.Done, "OK");
                _copyStep.SetState(StepState.Done, "OK");
                _launchStep.SetState(StepState.Active, "3");
                RefreshUi();
            }

            private Control BuildHeader()
            {
                var panel = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    ColumnCount = 2,
                    AutoSize = true,
                    Margin = new Padding(0, 0, 0, 22),
                    BackColor = Color.Transparent,
                };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

                var logo = new Label
                {
                    AutoSize = false,
                    Size = new Size(72, 72),
                    Margin = new Padding(0, 0, 18, 0),
                    Text = "K",
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 26F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.White,
                    BackColor = Color.FromArgb(10, 100, 216),
                };
                ApplyRoundedRegion(logo, 18);
                panel.Controls.Add(logo, 0, 0);

                var textPanel = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 2,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0),
                };
                textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                textPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var heading = new Label
                {
                    AutoSize = true,
                    Text = "Kivrio Agent UI s'installe sur votre PC",
                    Font = new Font("Segoe UI", 20F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(31, 31, 31),
                    Margin = new Padding(0, 2, 0, 8),
                };
                textPanel.Controls.Add(heading, 0, 0);

                var copy = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(460, 0),
                    Text = "Merci de patienter pendant la preparation de l'application. Kivrio Agent UI se lancera automatiquement des que l'installation sera terminee.",
                    Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(93, 93, 93),
                    Margin = new Padding(0),
                };
                textPanel.Controls.Add(copy, 0, 1);

                panel.Controls.Add(textPanel, 1, 0);
                return panel;
            }

            private static Panel BuildCard()
            {
                var card = new Panel
                {
                    Dock = DockStyle.Top,
                    BackColor = Color.White,
                };
                ApplyRoundedRegion(card, 14);
                return card;
            }

            private void RefreshUi()
            {
                if (!IsHandleCreated)
                {
                    return;
                }

                Refresh();
                Update();
                Application.DoEvents();
            }

            private static void ApplyRoundedRegion(Control control, int radius)
            {
                control.Resize += delegate { UpdateRoundedRegion(control, radius); };
                UpdateRoundedRegion(control, radius);
            }

            private static void MakeCircular(Control control)
            {
                control.Resize += delegate { UpdateCircularRegion(control); };
                UpdateCircularRegion(control);
            }

            private static GraphicsPath BuildRoundedRectangle(Rectangle bounds, int radius)
            {
                int diameter = radius * 2;
                var path = new GraphicsPath();

                path.StartFigure();
                path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();

                return path;
            }

            private static void UpdateRoundedRegion(Control control, int radius)
            {
                if (control.Width <= 0 || control.Height <= 0)
                {
                    return;
                }

                using (var path = BuildRoundedRectangle(new Rectangle(0, 0, control.Width, control.Height), radius))
                {
                    control.Region = new Region(path);
                }
            }

            private static void UpdateCircularRegion(Control control)
            {
                if (control.Width <= 0 || control.Height <= 0)
                {
                    return;
                }

                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(0, 0, control.Width, control.Height);
                    control.Region = new Region(path);
                }
            }
        }

        private enum StepState
        {
            Pending,
            Active,
            Done,
        }

        private sealed class StepRow : Panel
        {
            private readonly Label _badge;
            private readonly Label _title;
            private readonly Label _description;

            public StepRow(string title, string description)
            {
                Dock = DockStyle.Top;
                Height = 64;
                BackColor = Color.Transparent;
                Margin = new Padding(0, 0, 0, 10);

                _badge = new Label
                {
                    AutoSize = false,
                    Size = new Size(28, 28),
                    Location = new Point(0, 4),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                    BorderStyle = BorderStyle.FixedSingle,
                };
                MakeCircular(_badge);
                Controls.Add(_badge);

                _title = new Label
                {
                    AutoSize = true,
                    Location = new Point(42, 2),
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(31, 31, 31),
                    Text = title,
                };
                Controls.Add(_title);

                _description = new Label
                {
                    AutoSize = false,
                    Location = new Point(42, 22),
                    Size = new Size(480, 34),
                    Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
                    ForeColor = Color.FromArgb(88, 88, 88),
                    Text = description,
                };
                Controls.Add(_description);

                SetState(StepState.Pending, "1");
            }

            public void SetState(StepState state, string badgeText)
            {
                _badge.Text = badgeText;

                switch (state)
                {
                    case StepState.Done:
                        _badge.BackColor = Color.FromArgb(232, 243, 232);
                        _badge.ForeColor = Color.FromArgb(47, 122, 47);
                        _badge.FlatStyle = FlatStyle.Flat;
                        break;
                    case StepState.Active:
                        _badge.BackColor = Color.FromArgb(10, 100, 216);
                        _badge.ForeColor = Color.White;
                        _badge.FlatStyle = FlatStyle.Flat;
                        break;
                    default:
                        _badge.BackColor = Color.White;
                        _badge.ForeColor = Color.FromArgb(123, 135, 148);
                        _badge.FlatStyle = FlatStyle.Flat;
                        break;
                }
            }

            private static void MakeCircular(Control control)
            {
                control.Resize += delegate { UpdateCircularRegion(control); };
                UpdateCircularRegion(control);
            }

            private static void UpdateCircularRegion(Control control)
            {
                if (control.Width <= 0 || control.Height <= 0)
                {
                    return;
                }

                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(0, 0, control.Width, control.Height);
                    control.Region = new Region(path);
                }
            }
        }
    }
}
