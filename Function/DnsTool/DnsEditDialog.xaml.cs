using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CustomControlLibrary.CustomControl.Attribute.DataGrid;
using PackageManager.Models;

namespace PackageManager.Function.DnsTool
{
    public partial class DnsEditDialog : Window
    {
        public string DnsValue { get; private set; }
        public System.Collections.ObjectModel.ObservableCollection<DnsAddrItem> DnsItems { get; } = new System.Collections.ObjectModel.ObservableCollection<DnsAddrItem>();
        private DnsAddrItem editingItem;

        public DnsEditDialog(string adapterName, string currentDns)
        {
            InitializeComponent();
            AdapterNameText.Text = adapterName;
            InitSegments(currentDns);
            DataContext = this;
            foreach (var addr in ParseIpv4List(currentDns))
            {
                DnsItems.Add(new DnsAddrItem(this) { AdapterName = adapterName, Address = addr });
            }
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            if (DnsItems.Count == 0)
            {
                MessageBox.Show("请至少添加一个IPv4 DNS地址", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var allValid = DnsItems.All(i => IsValidIpv4(i.Address));
            if (!allValid)
            {
                MessageBox.Show("存在无效的IPv4地址，请检查", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            DnsValues = DnsItems.Select(i => i.Address).ToList();
            DnsValue = DnsValues.FirstOrDefault();
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void AddOrSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var text = string.Join(".", new[] { DnsSeg1.Text, DnsSeg2.Text, DnsSeg3.Text, DnsSeg4.Text });
            if (!IsValidIpv4(text))
            {
                MessageBox.Show("请输入有效的IPv4地址，如 8.8.8.8", "校验失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (editingItem == null)
            {
                DnsItems.Add(new DnsAddrItem(this) { AdapterName = AdapterNameText.Text, Address = text });
            }
            else
            {
                editingItem.Address = text;
                editingItem = null;
                AddOrSaveButton.Content = "添加地址";
            }
            DnsSeg1.Text = DnsSeg2.Text = DnsSeg3.Text = DnsSeg4.Text = string.Empty;
            DnsSeg1.Focus();
            DnsSeg1.SelectAll();
        }

        private void InitSegments(string currentDns)
        {
            SetupSegment(DnsSeg1);
            SetupSegment(DnsSeg2);
            SetupSegment(DnsSeg3);
            SetupSegment(DnsSeg4);
            DataObject.AddPastingHandler(DnsSeg1, OnFirstSegmentPasting);
            DnsSeg1.Focus();
            DnsSeg1.SelectAll();
        }

        private void SetupSegment(TextBox tb)
        {
            tb.PreviewTextInput += Segment_PreviewTextInput;
            tb.PreviewKeyDown += Segment_PreviewKeyDown;
            tb.TextChanged += Segment_TextChanged;
            tb.GotFocus += Segment_GotFocus;
        }

        private void Segment_GotFocus(object sender, RoutedEventArgs e)
        {
            var tb = (TextBox)sender;
            tb.SelectAll();
        }

        private void Segment_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.Text == ".")
            {
                e.Handled = true;
                MoveNext(tb);
                return;
            }
            if (!Regex.IsMatch(e.Text, "^[0-9]$"))
            {
                e.Handled = true;
            }
        }

        private void Segment_TextChanged(object sender, TextChangedEventArgs e)
        {
            var tb = (TextBox)sender;
            if (tb.Text.Length >= 3)
            {
                MoveNext(tb);
            }
        }

        private void Segment_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var tb = (TextBox)sender;
            if (e.Key == Key.Back && tb.SelectionLength == 0 && tb.CaretIndex == 0)
            {
                e.Handled = true;
                MovePrev(tb);
                return;
            }
        }

        private void OnFirstSegmentPasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var text = (string)e.DataObject.GetData(typeof(string));
                var first = ExtractFirstIpv4(text);
                if (first != null)
                {
                    var parts = first.Split('.');
                    DnsSeg1.Text = parts.ElementAtOrDefault(0) ?? string.Empty;
                    DnsSeg2.Text = parts.ElementAtOrDefault(1) ?? string.Empty;
                    DnsSeg3.Text = parts.ElementAtOrDefault(2) ?? string.Empty;
                    DnsSeg4.Text = parts.ElementAtOrDefault(3) ?? string.Empty;
                    e.CancelCommand();
                }
            }
        }

        private void MoveNext(TextBox tb)
        {
            if (tb == DnsSeg1) { DnsSeg2.Focus(); DnsSeg2.SelectAll(); }
            else if (tb == DnsSeg2) { DnsSeg3.Focus(); DnsSeg3.SelectAll(); }
            else if (tb == DnsSeg3) { DnsSeg4.Focus(); DnsSeg4.SelectAll(); }
        }

        private void MovePrev(TextBox tb)
        {
            if (tb == DnsSeg4) { DnsSeg3.Focus(); DnsSeg3.SelectAll(); }
            else if (tb == DnsSeg3) { DnsSeg2.Focus(); DnsSeg2.SelectAll(); }
            else if (tb == DnsSeg2) { DnsSeg1.Focus(); DnsSeg1.SelectAll(); }
        }

        private static string ExtractFirstIpv4(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var m = Regex.Match(s, @"(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})");
            if (!m.Success) return null;
            return m.Value;
        }

        private static System.Collections.Generic.IEnumerable<string> ParseIpv4List(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) yield break;
            foreach (var raw in s.Split(','))
            {
                var t = raw.Trim();
                var ipv4 = ExtractFirstIpv4(t);
                if (!string.IsNullOrEmpty(ipv4)) yield return ipv4;
            }
        }

        private static bool IsValidIpv4(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            var m = Regex.Match(input, @"^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$");
            if (!m.Success) return false;
            for (int i = 1; i <= 4; i++)
            {
                var v = int.Parse(m.Groups[i].Value);
                if (v < 0 || v > 255) return false;
            }
            return true;
        }

        public System.Collections.Generic.List<string> DnsValues { get; private set; } = new System.Collections.Generic.List<string>();

        public class DnsAddrItem
        {
            private readonly DnsEditDialog owner;
            public DnsAddrItem(DnsEditDialog owner) { this.owner = owner; EditCommand = new RelayCommand(Edit); DeleteCommand = new RelayCommand(Delete); }

            [DataGridColumn(1, DisplayName = "网卡", Width = "220", IsReadOnly = true)]
            public string AdapterName { get; set; }

            [DataGridColumn(2, DisplayName = "地址", Width = "180")]
            public string Address { get; set; }

            [DataGridMultiButton(nameof(ActionButtons), 3, DisplayName = "操作", Width = "220", ButtonSpacing = 12)]
            public string Actions { get; set; }

            public ICommand EditCommand { get; }
            public ICommand DeleteCommand { get; }

            public System.Collections.Generic.List<ButtonConfig> ActionButtons => new System.Collections.Generic.List<ButtonConfig>
            {
                new ButtonConfig { Text = "编辑", Width = 70, Height = 26, CommandProperty = nameof(EditCommand) },
                new ButtonConfig { Text = "删除", Width = 70, Height = 26, CommandProperty = nameof(DeleteCommand) },
            };

            private void Edit()
            {
                var parts = Address.Split('.');
                owner.DnsSeg1.Text = parts.ElementAtOrDefault(0) ?? string.Empty;
                owner.DnsSeg2.Text = parts.ElementAtOrDefault(1) ?? string.Empty;
                owner.DnsSeg3.Text = parts.ElementAtOrDefault(2) ?? string.Empty;
                owner.DnsSeg4.Text = parts.ElementAtOrDefault(3) ?? string.Empty;
                owner.editingItem = this;
                owner.AddOrSaveButton.Content = "保存修改";
                owner.DnsSeg1.Focus();
                owner.DnsSeg1.SelectAll();
            }

            private void Delete()
            {
                owner.DnsItems.Remove(this);
                if (ReferenceEquals(owner.editingItem, this))
                {
                    owner.editingItem = null;
                    owner.AddOrSaveButton.Content = "添加地址";
                }
            }
        }
    }
}
