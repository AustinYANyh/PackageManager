using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using PackageManager.Features.CodeWorkspace.Models;
using PackageManager.Models;

namespace PackageManager.Features.CodeWorkspace.Views
{
    public partial class PackageLinkSelectionWindow : Window, INotifyPropertyChanged
    {
        private PackageLinkOption _selectedPackage;
        private RepositoryLinkOption _selectedRepository;
        private object _selectedOption;
        private readonly bool _repositoryMode;

        public PackageLinkSelectionWindow(
            CodeRepository repository,
            IReadOnlyList<PackageLinkOption> packageOptions,
            PackageLinkOption suggestedPackage,
            PackageLinkOption currentPackage = null)
        {
            InitializeComponent();
            Repository = repository;
            PackageOptions = packageOptions ?? new List<PackageLinkOption>();
            SuggestedPackage = suggestedPackage;
            SelectedPackage = ResolveOption(currentPackage) ?? ResolveOption(suggestedPackage) ?? PackageOptions.FirstOrDefault();
            SelectedOption = SelectedPackage;
            DataContext = this;
        }

        public PackageLinkSelectionWindow(
            PackageInfo package,
            IReadOnlyList<RepositoryLinkOption> repositoryOptions,
            RepositoryLinkOption suggestedRepository = null)
        {
            InitializeComponent();
            _repositoryMode = true;
            PackageName = package?.ProductName;
            RepositoryOptions = repositoryOptions ?? new List<RepositoryLinkOption>();
            SuggestedRepository = suggestedRepository;
            SelectedRepository = ResolveOption(suggestedRepository) ?? RepositoryOptions.FirstOrDefault();
            SelectedOption = SelectedRepository;
            DataContext = this;
        }

        public event PropertyChangedEventHandler PropertyChanged;

        public CodeRepository Repository { get; }

        public IReadOnlyList<PackageLinkOption> PackageOptions { get; }

        public IReadOnlyList<RepositoryLinkOption> RepositoryOptions { get; }

        public PackageLinkOption SuggestedPackage { get; }

        public RepositoryLinkOption SuggestedRepository { get; }

        public string PackageName { get; }

        public PackageLinkOption SelectedPackage
        {
            get => _selectedPackage;
            set => SetProperty(ref _selectedPackage, value);
        }

        public RepositoryLinkOption SelectedRepository
        {
            get => _selectedRepository;
            set => SetProperty(ref _selectedRepository, value);
        }

        public object SelectedOption
        {
            get => _selectedOption;
            set
            {
                if (SetProperty(ref _selectedOption, value))
                {
                    SelectedPackage = value as PackageLinkOption;
                    SelectedRepository = value as RepositoryLinkOption;
                }
            }
        }

        public IEnumerable<object> SelectionOptions => _repositoryMode
            ? RepositoryOptions?.Cast<object>()
            : PackageOptions?.Cast<object>();

        public string SelectorTitle => _repositoryMode ? "源码仓库" : "产品包";

        public string TitleText => _repositoryMode ? "关联源码仓库" : "关联产品包";

        public string RepositoryText => _repositoryMode
            ? PackageName
            : (Repository == null ? string.Empty : $"{Repository.Name}  |  {Repository.Path}");

        public string SuggestionText => _repositoryMode
            ? (SuggestedRepository == null
                ? "未找到明确建议，请手动选择源码仓库。"
                : $"建议关联: {SuggestedRepository.Name}")
            : (SuggestedPackage == null
            ? "未找到明确建议，请手动选择产品包。"
                : $"建议关联: {SuggestedPackage.ProductName}");

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_repositoryMode && SelectedPackage == null)
            {
                MessageBox.Show("请选择要关联的产品包。", "关联产品包", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_repositoryMode && SelectedRepository == null)
            {
                MessageBox.Show("请选择要关联的源码仓库。", "关联源码仓库", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            DialogResult = true;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private PackageLinkOption ResolveOption(PackageLinkOption option)
        {
            if (option == null)
            {
                return null;
            }

            return PackageOptions.FirstOrDefault(item =>
                string.Equals(item.Key, option.Key, System.StringComparison.OrdinalIgnoreCase));
        }

        private RepositoryLinkOption ResolveOption(RepositoryLinkOption option)
        {
            if (option == null)
            {
                return null;
            }

            return RepositoryOptions.FirstOrDefault(item =>
                string.Equals(item.Key, option.Key, System.StringComparison.OrdinalIgnoreCase));
        }

        private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}
