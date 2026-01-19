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
    public partial class DebugOptionsWindow : Window
    {
        private readonly string _localPath;
        private JObject _data;
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

    public class DynamicPropertyBag : ICustomTypeDescriptor
    {
        private readonly JObject _data;
        private readonly PropertyDescriptorCollection _props;

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

        public AttributeCollection GetAttributes() => AttributeCollection.Empty;
        public string GetClassName() => nameof(DynamicPropertyBag);
        public string GetComponentName() => null;
        public TypeConverter GetConverter() => null;
        public EventDescriptor GetDefaultEvent() => null;
        public PropertyDescriptor GetDefaultProperty() => null;
        public object GetEditor(Type editorBaseType) => null;
        public EventDescriptorCollection GetEvents(Attribute[] attributes) => EventDescriptorCollection.Empty;
        public EventDescriptorCollection GetEvents() => EventDescriptorCollection.Empty;
        public PropertyDescriptorCollection GetProperties(Attribute[] attributes) => _props;
        public PropertyDescriptorCollection GetProperties() => _props;
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

    public class DictionaryPropertyDescriptor : PropertyDescriptor
    {
        private readonly JObject _data;
        private readonly string _name;

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

        public override bool CanResetValue(object component) => false;
        public override Type ComponentType => typeof(DynamicPropertyBag);
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
        public override bool IsReadOnly => false;
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
        public override void ResetValue(object component) { }
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
        public override bool ShouldSerializeValue(object component) => true;
    }

    public class JsonPathPropertyDescriptor : PropertyDescriptor
    {
        private readonly JObject _root;
        private readonly string[] _segments;
        private readonly string _displayName;
        private readonly string _category;

        public JsonPathPropertyDescriptor(JObject root, string path, string displayName, string category)
            : base(displayName, new Attribute[] { new DisplayNameAttribute(displayName), new CategoryAttribute(category) })
        {
            _root = root ?? new JObject();
            _segments = (path ?? displayName)?.Split('.') ?? new[] { displayName };
            _displayName = displayName;
            _category = category;
        }

        public override bool CanResetValue(object component) => false;
        public override Type ComponentType => typeof(DynamicPropertyBag);
        public override bool IsReadOnly => false;
        public override bool ShouldSerializeValue(object component) => true;
        public override void ResetValue(object component) { }

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
