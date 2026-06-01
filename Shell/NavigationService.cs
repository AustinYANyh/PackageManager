using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using PackageManager.Services;
using PackageManager.Views;

namespace PackageManager.Shell
{
    public class NavigationService : INotifyPropertyChanged
    {
        private readonly Frame _frame;
        private readonly ToolRegistry _registry;
        private Page _homePage;
        private Func<Page> _homePageFactory;
        private int _navigationVersion;
        private bool _isHomeActive;
        private string _currentKey;

        public NavigationService(Frame frame, ToolRegistry registry)
        {
            _frame = frame;
            _registry = registry;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<string> Navigated;

        public int NavigationVersion
        {
            get => _navigationVersion;
            private set { _navigationVersion = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NavigationVersion))); }
        }

        public bool IsHomeActive
        {
            get => _isHomeActive;
            private set { _isHomeActive = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHomeActive))); }
        }

        public string CurrentKey
        {
            get => _currentKey;
            private set { _currentKey = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentKey))); }
        }

        public ToolRegistry Registry => _registry;

        public void SetHomePageFactory(Func<Page> factory)
        {
            _homePageFactory = factory;
        }

        public void NavigateHome()
        {
            if (_homePage == null && _homePageFactory != null)
            {
                _homePage = _homePageFactory();
            }

            if (_homePage != null)
            {
                _frame.Navigate(_homePage);
                NavigationVersion++;
                IsHomeActive = true;
                CurrentKey = null;
                Navigated?.Invoke("仪表盘");
            }
        }

        public bool NavigateTo(string key)
        {
            var descriptor = _registry.FindByKey(key);
            if (descriptor == null) return false;

            try
            {
                var page = descriptor.Factory();
                if (page == null) return false;

                if (page is ICentralPage icp)
                {
                    icp.RequestExit += () => NavigateHome();
                }

                _frame.Navigate(page);
                NavigationVersion++;
                IsHomeActive = false;
                CurrentKey = key;
                Navigated?.Invoke(descriptor.DisplayName);
                return true;
            }
            catch (Exception ex)
            {
                LoggingService.LogError(ex, $"导航到 {descriptor.DisplayName} 失败");
                MessageBox.Show($"打开{descriptor.DisplayName}失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        public void NavigateToPage(Page page, string displayName = null)
        {
            if (page == null) return;

            if (page is ICentralPage icp)
            {
                icp.RequestExit += () => NavigateHome();
            }

            _frame.Navigate(page);
            NavigationVersion++;
            IsHomeActive = false;
            CurrentKey = null;
            if (displayName != null)
            {
                Navigated?.Invoke(displayName);
            }
        }
    }
}
