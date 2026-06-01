using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using PackageManager.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PackageManager.Function.Setting
{
    /// <summary>
    /// 调试选项窗口，用于查看和编辑 DebugSetting.json 配置文件。
    /// </summary>
    public partial class DebugOptionsWindow : Window
    {
        private readonly string _localPath;
        private JObject _data;

        /// <summary>
        /// 初始化 <see cref="DebugOptionsWindow"/> 的新实例。
        /// </summary>
        /// <param name="localPath">本地包路径。</param>
        public DebugOptionsWindow(string localPath)
        {
            InitializeComponent();
            _localPath = localPath;
            LoadData();
        }

        private string GetDebugSettingPath()
        {
            return System.IO.Path.Combine(_localPath ?? string.Empty, "config", "DebugSetting.json");
        }

        private void LoadData()
        {
            var p = GetDebugSettingPath();
            if (File.Exists(p))
            {
                var json = File.ReadAllText(p);
                _data = string.IsNullOrWhiteSpace(json) ? new JObject() : JObject.Parse(json);
            }
            else
            {
                _data = new JObject();
            }

            PropertyGrid.SelectedObject = new DynamicPropertyBag(_data);
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            var dir = System.IO.Path.Combine(_localPath ?? string.Empty, "config");
            Directory.CreateDirectory(dir);
            var path = GetDebugSettingPath();
            var json = _data.ToString(Formatting.Indented);
            File.WriteAllText(path, json);
            LoggingService.LogInfo($"调试配置已保存: {path}");
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    /// <summary>
    /// 基于 <see cref="JObject"/> 的动态属性容器，将 JSON 数据映射为可编辑的属性集合。
    /// 实现 <see cref="ICustomTypeDescriptor"/> 以支持 PropertyGrid 编辑。
    /// </summary>
    public class DynamicPropertyBag : ICustomTypeDescriptor
    {
        private readonly JObject _data;
        private readonly PropertyDescriptorCollection _props;

        /// <summary>
        /// 初始化 <see cref="DynamicPropertyBag"/> 的新实例。
        /// </summary>
        /// <param name="data">包含属性数据的 JSON 对象。</param>
        public DynamicPropertyBag(JObject data)
        {
            _data = data ?? new JObject();
            var list = new List<PropertyDescriptor>();
            foreach (var p in _data.Properties())
            {
                var v = p.Value;
                if (v == null)
                {
                    list.Add(new DictionaryPropertyDescriptor(_data, p.Name));
                    continue;
                }
                if (v.Type == JTokenType.Object)
                {
                    AddLeafDescriptorsForObject(_data, (JObject)v, p.Name, list);
                }
                else
                {
                    list.Add(new DictionaryPropertyDescriptor(_data, p.Name));
                }
            }
            _props = new PropertyDescriptorCollection(list.ToArray());
        }

        /// <summary>
        /// 获取组件的自定义属性集合。
        /// </summary>
        /// <returns>组件的属性集合。</returns>
        public AttributeCollection GetAttributes() => AttributeCollection.Empty;
        /// <summary>
        /// 获取此类的名称。
        /// </summary>
        /// <returns>类名称。</returns>
        public string GetClassName() => nameof(DynamicPropertyBag);
        /// <summary>
        /// 获取组件名称。
        /// </summary>
        /// <returns>组件名称，此实现始终返回 <c>null</c>。</returns>
        public string GetComponentName() => null;
        /// <summary>
        /// 获取类型转换器。
        /// </summary>
        /// <returns>类型转换器，此实现始终返回 <c>null</c>。</returns>
        public TypeConverter GetConverter() => null;
        /// <summary>
        /// 获取默认事件。
        /// </summary>
        /// <returns>默认事件，此实现始终返回 <c>null</c>。</returns>
        public EventDescriptor GetDefaultEvent() => null;
        /// <summary>
        /// 获取默认属性。
        /// </summary>
        /// <returns>默认属性，此实现始终返回 <c>null</c>。</returns>
        public PropertyDescriptor GetDefaultProperty() => null;
        /// <summary>
        /// 获取指定基类型的编辑器。
        /// </summary>
        /// <param name="editorBaseType">编辑器的基类型。</param>
        /// <returns>编辑器实例，此实现始终返回 <c>null</c>。</returns>
        public object GetEditor(Type editorBaseType) => null;
        /// <summary>
        /// 获取匹配指定属性筛选条件的事件集合。
        /// </summary>
        /// <param name="attributes">用于筛选事件的属性数组。</param>
        /// <returns>匹配的事件集合。</returns>
        public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
        /// <summary>
        /// 获取事件集合。
        /// </summary>
        /// <returns>事件集合。</returns>
        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
        /// <summary>
        /// 获取匹配指定属性筛选条件的属性描述符集合。
        /// </summary>
        /// <param name="attributes">用于筛选属性的属性数组。</param>
        /// <returns>属性描述符集合。</returns>
        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) => _props;
        /// <summary>
        /// 获取属性描述符集合。
        /// </summary>
        /// <returns>属性描述符集合。</returns>
        public PropertyDescriptorCollection GetProperties() => _props;
        /// <summary>
        /// 获取指定属性描述符所属的对象。
        /// </summary>
        /// <param name="pd">属性描述符。</param>
        /// <returns>属性所有者对象。</returns>
        public object GetPropertyOwner(PropertyDescriptor pd) => this;

        private static void AddLeafDescriptorsForObject(JObject root, JObject obj, string category, List<PropertyDescriptor> list)
        {
            foreach (var prop in obj.Properties())
            {
                var val = prop.Value;
                var path = $"{category}.{prop.Name}";
                if (val == null)
                {
                    list.Add(new JsonPathPropertyDescriptor(root, path, prop.Name, category));
                    continue;
                }
                if (val.Type == JTokenType.Object)
                {
                    AddLeafDescriptorsForObject(root, (JObject)val, $"{category}.{prop.Name}", list);
                }
                else
                {
                    list.Add(new JsonPathPropertyDescriptor(root, path, prop.Name, category));
                }
            }
        }
    }

    /// <summary>
    /// 将 <see cref="JObject"/> 中顶层键映射为属性的属性描述符，
    /// 支持 PropertyGrid 中直接编辑 JSON 顶层值。
    /// </summary>
    public class DictionaryPropertyDescriptor : PropertyDescriptor
    {
        private readonly JObject _data;
        private readonly string _name;

            /// <summary>
            /// 初始化 <see cref="DictionaryPropertyDescriptor"/> 的新实例。
            /// </summary>
            /// <param name="data">包含属性数据的 JSON 对象。</param>
            /// <param name="name">属性名称。</param>
            public DictionaryPropertyDescriptor(JObject data, string name) : base(
                name,
                BuildAttributes(data, name))
        {
            _data = data;
            _name = name;
        }

            private static Attribute[] BuildAttributes(JObject data, string name)
            {
                var attrs = new List<Attribute>
                {
                    new DisplayNameAttribute(name),
                    new CategoryAttribute("调试")
                };

                var token = data?[name];
                if (token != null && token.Type == JTokenType.Object)
                {
                    attrs.Add(new TypeConverterAttribute(typeof(ExpandableObjectConverter)));
                }

                return attrs.ToArray();
            }

        /// <summary>
        /// 获取一个值，指示是否可以重置该属性的值。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>始终返回 <c>false</c>。</returns>
        public override bool CanResetValue(object component) => false;
        /// <summary>
        /// 获取该属性所属的组件类型。
        /// </summary>
        public override Type ComponentType => typeof(DynamicPropertyBag);
        /// <summary>
        /// 获取属性的当前值，根据 JSON 值类型进行类型转换。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>属性值的类型转换结果。</returns>
        public override object GetValue(object component)
        {
            var t = _data[_name];
            if (t == null) return null;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Float) return t.Value<double>();
            if (t.Type == JTokenType.Object) return new DynamicPropertyBag((JObject)t);
            return t.ToString();
        }
        /// <summary>
        /// 获取一个值，指示该属性是否为只读。
        /// </summary>
        public override bool IsReadOnly => false;
        /// <summary>
        /// 获取属性的类型，根据 JSON 值类型推断。
        /// </summary>
        public override Type PropertyType
        {
            get
            {
                var t = _data[_name];
                if (t == null) return typeof(string);
                if (t.Type == JTokenType.Boolean) return typeof(bool);
                if (t.Type == JTokenType.Integer) return typeof(long);
                if (t.Type == JTokenType.Float) return typeof(double);
                if (t.Type == JTokenType.Object) return typeof(DynamicPropertyBag);
                return typeof(string);
            }
        }
        /// <summary>
        /// 重置该属性的值为默认值。此实现不执行任何操作。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        public override void ResetValue(object component) { }
        /// <summary>
        /// 设置属性的值，根据 JSON 值类型进行类型转换后写入。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <param name="value">要设置的新值。</param>
        public override void SetValue(object component, object value)
        {
            var t = _data[_name];
            if (t == null)
            {
                _data[_name] = JToken.FromObject(value ?? string.Empty);
                return;
            }
            if (t.Type == JTokenType.Boolean)
            {
                bool v;
                if (value is bool b) v = b; else v = string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
                _data[_name] = v;
                return;
            }
            if (t.Type == JTokenType.Integer)
            {
                long v;
                if (value is long l) v = l; else long.TryParse(Convert.ToString(value), out v);
                _data[_name] = v;
                return;
            }
            if (t.Type == JTokenType.Float)
            {
                double v;
                if (value is double d) v = d; else double.TryParse(Convert.ToString(value), out v);
                _data[_name] = v;
                return;
            }
            if (t.Type == JTokenType.Object)
            {
                if (value is JObject jo)
                {
                    _data[_name] = jo;
                    return;
                }
                var s = Convert.ToString(value);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    try
                    {
                        _data[_name] = JObject.Parse(s);
                    }
                    catch
                    {
                        _data[_name] = new JObject();
                    }
                }
                else
                {
                    _data[_name] = new JObject();
                }
                return;
            }
            _data[_name] = JToken.FromObject(value ?? string.Empty);
        }
        /// <summary>
        /// 获取一个值，指示该属性的值是否需要序列化。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>始终返回 <c>true</c>。</returns>
        public override bool ShouldSerializeValue(object component) => true;
    }

    /// <summary>
    /// 通过点分隔路径将嵌套 JSON 属性映射为可编辑属性的属性描述符，
    /// 支持深层 JSON 结构的 PropertyGrid 编辑。
    /// </summary>
    public class JsonPathPropertyDescriptor : PropertyDescriptor
    {
        private readonly JObject _root;
        private readonly string[] _segments;
        private readonly string _displayName;
        private readonly string _category;

        /// <summary>
        /// 初始化 <see cref="JsonPathPropertyDescriptor"/> 的新实例。
        /// </summary>
        /// <param name="root">JSON 根对象。</param>
        /// <param name="path">点分隔的属性路径。</param>
        /// <param name="displayName">属性显示名称。</param>
        /// <param name="category">属性分类名称。</param>
        public JsonPathPropertyDescriptor(JObject root, string path, string displayName, string category)
            : base(displayName, new Attribute[] { new DisplayNameAttribute(displayName), new CategoryAttribute(category) })
        {
            _root = root ?? new JObject();
            _segments = (path ?? displayName)?.Split('.') ?? new[] { displayName };
            _displayName = displayName;
            _category = category;
        }

        /// <summary>
        /// 获取一个值，指示是否可以重置该属性的值。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>始终返回 <c>false</c>。</returns>
        public override bool CanResetValue(object component) => false;
        /// <summary>
        /// 获取该属性所属的组件类型。
        /// </summary>
        public override Type ComponentType => typeof(DynamicPropertyBag);
        /// <summary>
        /// 获取一个值，指示该属性是否为只读。
        /// </summary>
        public override bool IsReadOnly => false;
        /// <summary>
        /// 获取一个值，指示该属性的值是否需要序列化。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>始终返回 <c>true</c>。</returns>
        public override bool ShouldSerializeValue(object component) => true;
        /// <summary>
        /// 重置该属性的值为默认值。此实现不执行任何操作。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        public override void ResetValue(object component) { }

        /// <summary>
        /// 获取属性的类型，根据 JSON 值类型推断。
        /// </summary>
        public override Type PropertyType
        {
            get
            {
                var t = GetToken();
                if (t == null) return typeof(string);
                if (t.Type == JTokenType.Boolean) return typeof(bool);
                if (t.Type == JTokenType.Integer) return typeof(long);
                if (t.Type == JTokenType.Float) return typeof(double);
                if (t.Type == JTokenType.Array) return typeof(string);
                return typeof(string);
            }
        }

        /// <summary>
        /// 获取属性的当前值，根据 JSON 值类型进行类型转换。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <returns>属性值的类型转换结果。</returns>
        public override object GetValue(object component)
        {
            var t = GetToken();
            if (t == null) return null;
            if (t.Type == JTokenType.Boolean) return t.Value<bool>();
            if (t.Type == JTokenType.Integer) return t.Value<long>();
            if (t.Type == JTokenType.Float) return t.Value<double>();
            if (t.Type == JTokenType.Array)
            {
                var arr = (JArray)t;
                return string.Join(",", arr.Select(x => x.Type == JTokenType.String ? x.Value<string>() : x.ToString()));
            }
            return t.ToString();
        }

        /// <summary>
        /// 设置属性的值，通过点分隔路径写入嵌套 JSON 结构。
        /// </summary>
        /// <param name="component">拥有该属性的组件。</param>
        /// <param name="value">要设置的新值。</param>
        public override void SetValue(object component, object value)
        {
            var parent = EnsureParentObject();
            var leaf = _segments[_segments.Length - 1];
            var current = parent[leaf];
            if (current == null)
            {
                parent[leaf] = JToken.FromObject(value ?? string.Empty);
                return;
            }

            switch (current.Type)
            {
                case JTokenType.Boolean:
                    bool vb = value is bool b ? b : string.Equals(Convert.ToString(value), "true", StringComparison.OrdinalIgnoreCase);
                    parent[leaf] = vb;
                    break;
                case JTokenType.Integer:
                    long vl;
                    if (value is long l) vl = l; else long.TryParse(Convert.ToString(value), out vl);
                    parent[leaf] = vl;
                    break;
                case JTokenType.Float:
                    double vd;
                    if (value is double d) vd = d; else double.TryParse(Convert.ToString(value), out vd);
                    parent[leaf] = vd;
                    break;
                case JTokenType.Array:
                    var s = Convert.ToString(value) ?? string.Empty;
                    var parts = s.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(x => x.Trim())
                                 .ToArray();
                    var arr = new JArray();
                    foreach (var p in parts)
                    {
                        if (long.TryParse(p, out var li))
                        {
                            arr.Add(li);
                        }
                        else if (double.TryParse(p, out var dd))
                        {
                            arr.Add(dd);
                        }
                        else
                        {
                            arr.Add(p);
                        }
                    }
                    parent[leaf] = arr;
                    break;
                default:
                    parent[leaf] = JToken.FromObject(value ?? string.Empty);
                    break;
            }
        }

        private JToken GetToken()
        {
            JToken t = _root;
            foreach (var seg in _segments)
            {
                if (t == null || t.Type != JTokenType.Object)
                {
                    return null;
                }
                t = t[seg];
            }
            return t;
        }

        private JObject EnsureParentObject()
        {
            JToken t = _root;
            for (int i = 0; i < _segments.Length - 1; i++)
            {
                var seg = _segments[i];
                var next = t[seg];
                if (next == null || next.Type != JTokenType.Object)
                {
                    var created = new JObject();
                    ((JObject)t)[seg] = created;
                    t = created;
                }
                else
                {
                    t = next;
                }
            }
            return (JObject)t;
        }
    }
}
