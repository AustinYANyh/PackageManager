using System.Collections.ObjectModel;
using PackageManager.Models;

namespace PackageManager.Function.Path
{
    public class ProductGroup
    {
        public string Name { get; set; }
        public ObservableCollection<LocalPathInfo> Children { get; set; } = new ObservableCollection<LocalPathInfo>();
    }
}

