using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Models;

namespace PackageManager.Function.DnsTool
{
    public partial class DnsSettingsWindow : Window
    {
        public DnsSettingsWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadAdapters();
        }

        public ObservableCollection<AdapterItem> AdapterItems { get; } = new ObservableCollection<AdapterItem>();

        public void EditDns(AdapterItem item)
        {
            var dlg = new DnsEditDialog(item.Name, item.DnsServers) { Owner = this };
            var ok = dlg.ShowDialog();
            if (ok == true)
            {
                var list = dlg.DnsValues;
                if (list != null && list.Count > 0)
                {
                    if (SetAdapterDnsList(item.Name, list))
                    {
                        RefreshAdapter(item.Name);
                    }
                    else
                    {
                        MessageBox.Show("设置DNS失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        public void ResetDns(AdapterItem item)
        {
            if (ResetAdapterDns(item.Name))
            {
                RefreshAdapter(item.Name);
            }
            else
            {
                MessageBox.Show("重置DNS失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void OpenDnsUi(AdapterItem item)
        {
            try
            {
                var uri = "ms-settings:network-status";
                var type = item.Type ?? string.Empty;
                if (type.Equals("Ethernet", System.StringComparison.OrdinalIgnoreCase))
                {
                    uri = "ms-settings:network-ethernet";
                }
                else if (type.IndexOf("Wireless", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                         type.Equals("Wireless80211", System.StringComparison.OrdinalIgnoreCase))
                {
                    uri = "ms-settings:network-wifi";
                }

                Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
                Process.Start(new ProcessStartInfo { FileName = "ncpa.cpl", UseShellExecute = true });
            }
            catch (System.Exception ex)
            {
                MessageBox.Show($"无法打开系统网络设置: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadAdapters()
        {
            AdapterItems.Clear();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var ipProps = nic.GetIPProperties();
                var ipv4s = ipProps.UnicastAddresses
                                   .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                   .Select(a => a.Address.ToString());
                var dns = ipProps.DnsAddresses.Select(a => a.ToString());
                var connName = nic.Name;

                AdapterItems.Add(new AdapterItem(this)
                {
                    Name = connName,
                    Description = nic.Description,
                    Type = nic.NetworkInterfaceType.ToString(),
                    Status = nic.OperationalStatus.ToString(),
                    IPv4 = string.Join(", ", ipv4s),
                    DnsServers = string.Join(", ", dns),
                    ConnectionName = connName,
                });
            }
        }

        private void RefreshAdapter(string name)
        {
            try
            {
                var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == name);
                if (nic == null)
                {
                    return;
                }

                var ipProps = nic.GetIPProperties();
                var dns = string.Join(", ", ipProps.DnsAddresses.Select(a => a.ToString()));
                var item = AdapterItems.FirstOrDefault(a => a.Name == name);
                if (item != null)
                {
                    item.DnsServers = dns;
                }
            }
            catch
            {
            }
        }

        private bool SetAdapterDns(string name, string dns)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ip set dns name=\"{name}\" static {dns}",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private bool SetAdapterDnsList(string name, System.Collections.Generic.List<string> addrs)
        {
            try
            {
                // Clear all existing DNS entries
                var del = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ip delete dns name=\"{name}\" all",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                var pd = System.Diagnostics.Process.Start(del);
                pd.WaitForExit();

                // Set primary
                if (addrs.Count > 0)
                {
                    var set = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"interface ip set dns name=\"{name}\" static {addrs[0]}",
                        UseShellExecute = true,
                        Verb = "runas",
                    };
                    var ps = System.Diagnostics.Process.Start(set);
                    ps.WaitForExit();
                    if (ps.ExitCode != 0) return false;
                }

                // Add additional entries
                for (int i = 1; i < addrs.Count; i++)
                {
                    var add = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "netsh",
                        Arguments = $"interface ip add dns name=\"{name}\" {addrs[i]} index={i + 1}",
                        UseShellExecute = true,
                        Verb = "runas",
                    };
                    var pa = System.Diagnostics.Process.Start(add);
                    pa.WaitForExit();
                    if (pa.ExitCode != 0) return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool ResetAdapterDns(string name)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"interface ip set dns name=\"{name}\" dhcp",
                    UseShellExecute = true,
                    Verb = "runas",
                };
                var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        public class AdapterItem
        {
            private readonly DnsSettingsWindow owner;

            public AdapterItem(DnsSettingsWindow owner)
            {
                this.owner = owner;
                OpenCommand = new RelayCommand(() => owner.OpenDnsUi(this));
                EditCommand = new RelayCommand(() => owner.EditDns(this));
                ResetCommand = new RelayCommand(() => owner.ResetDns(this));
            }

            [DataGridColumn(1, DisplayName = "网卡名称", Width = "220", IsReadOnly = true)]
            public string Name { get; set; }

            [DataGridColumn(2, DisplayName = "描述", Width = "320", IsReadOnly = true)]
            public string Description { get; set; }

            [DataGridColumn(3, DisplayName = "类型", Width = "140", IsReadOnly = true)]
            public string Type { get; set; }

            [DataGridColumn(4, DisplayName = "状态", Width = "120", IsReadOnly = true)]
            public string Status { get; set; }

            [DataGridColumn(5, DisplayName = "IPv4地址", Width = "200", IsReadOnly = true)]
            public string IPv4 { get; set; }

            [DataGridColumn(6, DisplayName = "DNS服务器", Width = "340", IsReadOnly = true)]
            public string DnsServers { get; set; }

            [DataGridMultiButton(nameof(ActionButtons), 7, DisplayName = "操作DNS", Width = "350", ButtonSpacing = 12)]
            public string Actions { get; set; }

            public ICommand EditCommand { get; }

            public ICommand ResetCommand { get; }

            public ICommand OpenCommand { get; }

            public string ConnectionName { get; set; }

            public System.Collections.Generic.List<ButtonConfig> ActionButtons => new System.Collections.Generic.List<ButtonConfig>
            {
                new ButtonConfig { Text = "打开", Width = 70, Height = 26, CommandProperty = nameof(OpenCommand) },
                new ButtonConfig { Text = "编辑", Width = 70, Height = 26, CommandProperty = nameof(EditCommand) },
                new ButtonConfig { Text = "重置", Width = 70, Height = 26, CommandProperty = nameof(ResetCommand) },
            };
        }
    }
}
