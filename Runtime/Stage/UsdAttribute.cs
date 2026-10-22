using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Unity.USDToolkit.Native;
using UnityEngine;

namespace Unity.USDToolkit
{
    /// <summary>An attribute's value type, roles aside (a point3f[] is a <see cref="Float3"/> array).</summary>
    public enum UsdValueType
    {
        /// <summary>A type this API has no form for (opaque, a dictionary, ...).</summary>
        None = 0,
        Bool = 1,
        UChar = 2,
        Int = 3,
        UInt = 4,
        Int64 = 5,
        UInt64 = 6,
        Half = 7,
        Float = 8,
        Double = 9,
        TimeCode = 10,
        String = 11,
        Token = 12,
        Asset = 13,
        Int2 = 14,
        Int3 = 15,
        Int4 = 16,
        Half2 = 17,
        Half3 = 18,
        Half4 = 19,
        Float2 = 20,
        Float3 = 21,
        Float4 = 22,
        Double2 = 23,
        Double3 = 24,
        Double4 = 25,
        Quath = 26,
        Quatf = 27,
        Quatd = 28,
        Matrix2d = 29,
        Matrix3d = 30,
        Matrix4d = 31,
    }

    /// <summary>
    /// A prim's attribute, by name. <see cref="Get{T}"/> and <see cref="Set{T}"/> read and write it as
    /// C# and Unity types: an array attribute as an array (<c>Vector3[]</c> for point3f[]), a single
    /// value as itself. The shape and kind must match what the attribute holds -- an int is never
    /// read as a float, a float3 never as a float4 -- while precision converts: a double3 reads as a
    /// Vector3, a half writes from a float. <see cref="GetComponents"/> reads any real-valued
    /// attribute at full precision. Values are USD's own: right-handed, in stage units.
    /// <list type="table">
    /// <listheader><term>C# type</term><description>USD types</description></listheader>
    /// <item><term>bool, byte, int, uint, long, ulong</term><description>bool, uchar, int, uint, int64, uint64</description></item>
    /// <item><term>float, double</term><description>half, float, double, timecode</description></item>
    /// <item><term>string</term><description>string, token, asset (the authored path; see <see cref="GetResolvedPath"/>)</description></item>
    /// <item><term>Vector2, Vector3, Vector4</term><description>half2-4, float2-4, double2-4, any role (point, normal, color, texCoord)</description></item>
    /// <item><term>Color</term><description>color3 (alpha 1) or color4, of any precision</description></item>
    /// <item><term>Quaternion</term><description>quath, quatf, quatd</description></item>
    /// <item><term>Matrix4x4</term><description>matrix4d, as column vectors (transposed from USD's rows)</description></item>
    /// <item><term>Vector2Int, Vector3Int</term><description>int2, int3</description></item>
    /// </list>
    /// and arrays of each.
    /// </summary>
    public readonly struct UsdAttribute
    {
        internal UsdAttribute(UsdPrim prim, string name)
        {
            Prim = prim;
            Name = name;
        }

        public UsdPrim Prim { get; }

        public string Name { get; }

        public UsdStage Stage => Prim.Stage;

        /// <summary>"/World/Mesh.points".</summary>
        public string Path => Prim.Path + "." + Name;

        /// <summary>Whether the prim has this attribute (authored, or from its schema).</summary>
        public bool Exists => Info.Exists != 0;

        public UsdValueType ValueType => (UsdValueType)(Info.ValueType & 0xff);

        public bool IsArray => (Info.ValueType & UsdNative.ValueArray) != 0;

        /// <summary>The declared type as a stage spells it ("point3f[]", "color3f"); empty when it does not exist.</summary>
        public string TypeName
        {
            get
            {
                UsdPrim prim = Prim;
                string text = prim.Stage.Run(context => UsdNative.QueryText(context, UsdNative.QueryProperties, prim.Path));
                foreach (string[] record in UsdPrim.Records(text))
                {
                    if (record.Length >= 3 && record[0] == Name && record[1] == "a")
                    {
                        return record[2];
                    }
                }

                return string.Empty;
            }
        }

        /// <summary>An authored value or a schema fallback.</summary>
        public bool HasValue => Info.HasValue != 0;

        public bool HasAuthoredValue => Info.HasAuthoredValue != 0;

        public bool IsUniform => Info.Uniform != 0;

        public bool IsCustom => Info.Custom != 0;

        public int TimeSampleCount => Info.TimeSampleCount;

        /// <summary>The time codes of the attribute's samples, ascending.</summary>
        public double[] GetTimeSamples()
        {
            UsdPrim prim = Prim;
            string name = Name;
            return prim.Stage.Run(context => UsdNative.GetAttributeTimeSamples(context, prim.Path, name));
        }

        /// <summary>
        /// The value at <paramref name="time"/> as <typeparamref name="T"/> (see the table above).
        /// Throws when there is no value (none authored, or blocked, and no schema fallback) or the
        /// attribute holds another shape or kind.
        /// </summary>
        public T Get<T>(UsdTimeCode time = default)
        {
            if (!TryGet(out T value, time))
            {
                throw new UsdStageException($"{Path} has no value at {time}.");
            }

            return value;
        }

        /// <summary>Like <see cref="Get{T}"/>, false (and no exception) when there is no value.</summary>
        public bool TryGet<T>(out T value, UsdTimeCode time = default)
        {
            Type type = typeof(T);
            bool array = type.IsArray;
            if (!UsdValueCodec.TryRead(this, array ? type.GetElementType() : type, array, time, out object read))
            {
                value = default;
                return false;
            }

            value = (T)read;
            return true;
        }

        /// <summary>
        /// Writes <paramref name="value"/> at <paramref name="time"/> in the stage's edit target. An
        /// attribute that does not exist yet is made, of <paramref name="typeName"/> ("point3f[]",
        /// "color3f", "token"), or by default the plain type of <typeparamref name="T"/> (float3[] for
        /// Vector3[], color3f for Color, string for string). An existing one keeps its declared type,
        /// which must take <typeparamref name="T"/>.
        /// </summary>
        public void Set<T>(T value, UsdTimeCode time = default, string typeName = null)
        {
            Type type = typeof(T);
            bool array = type.IsArray;
            if (array && value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            UsdValueCodec.Write(this, value, array ? type.GetElementType() : type, array, time, typeName, 0);
        }

        /// <summary>
        /// The components of a real-valued attribute (half, float, double, timecode, their vectors,
        /// quaternions as x, y, z, w, matrices row by row), every element's in turn, at full
        /// precision. Null when there is no value.
        /// </summary>
        public double[] GetComponents(UsdTimeCode time = default) => UsdValueCodec.ReadComponents(this, time);

        /// <summary>
        /// Writes the components of an existing real-valued attribute, as <see cref="GetComponents"/>
        /// lays them out; their count fixes the array length.
        /// </summary>
        public void SetComponents(double[] components, UsdTimeCode time = default) =>
            UsdValueCodec.WriteComponents(this, components ?? throw new ArgumentNullException(nameof(components)), time);

        /// <summary>An asset attribute's path as OpenUSD resolves it; empty when it does not resolve.</summary>
        public string GetResolvedPath(UsdTimeCode time = default) =>
            UsdValueCodec.ReadResolved(this, time, false) is string[] paths && paths.Length > 0 ? paths[0] : string.Empty;

        /// <summary>An asset array's paths as OpenUSD resolves them.</summary>
        public string[] GetResolvedPaths(UsdTimeCode time = default) =>
            UsdValueCodec.ReadResolved(this, time, true) ?? Array.Empty<string>();

        /// <summary>
        /// Blocks the value: at <paramref name="time"/>, or at the default with every sample when
        /// <paramref name="time"/> is the default. A blocked attribute reads its schema fallback, or
        /// has no value when it has none.
        /// </summary>
        public void Block(UsdTimeCode time = default) => UsdValueCodec.Write(this, null, null, false, time, null, UsdNative.SetBlock);

        /// <summary>Clears the edit target's value at <paramref name="time"/> (the default value at the default).</summary>
        public void ClearAt(UsdTimeCode time) => UsdValueCodec.Write(this, null, null, false, time, null, UsdNative.SetClear);

        /// <summary>Clears every value the edit target holds for the attribute: its default and its samples.</summary>
        public void Clear()
        {
            UsdPrim prim = Prim;
            string name = Name;
            prim.Stage.Edit(context => UsdNative.EditPrim(context, prim.Path, UsdNative.EditClearValue, name));
        }

        private RUsdAttributeInfo Info
        {
            get
            {
                UsdPrim prim = Prim;
                string name = Name;
                if (prim.Stage == null || prim.Path == null)
                {
                    throw new InvalidOperationException("This UsdAttribute's prim stands for no prim.");
                }

                return prim.Stage.Run(context => UsdNative.GetAttributeInfo(context, prim.Path, name));
            }
        }

        public override string ToString() => Path;
    }

    // Between C# values and the wire forms of the native value calls. The native side reads and
    // writes only the exact type an attribute holds; here the C# type is matched to that type's
    // shape, converting precision only.
    internal static class UsdValueCodec
    {
        private enum Shape
        {
            None,
            Bool,
            UChar,
            Int,
            UInt,
            Int64,
            UInt64,
            Real,
            Text,
            Real2,
            Real3,
            Real4,
            Quat,
            Matrix4,
            Int2,
            Int3,
            Color,
        }

        private static Shape ShapeOfType(Type element)
        {
            if (element == typeof(bool)) return Shape.Bool;
            if (element == typeof(byte)) return Shape.UChar;
            if (element == typeof(int)) return Shape.Int;
            if (element == typeof(uint)) return Shape.UInt;
            if (element == typeof(long)) return Shape.Int64;
            if (element == typeof(ulong)) return Shape.UInt64;
            if (element == typeof(float) || element == typeof(double)) return Shape.Real;
            if (element == typeof(string)) return Shape.Text;
            if (element == typeof(Vector2)) return Shape.Real2;
            if (element == typeof(Vector3)) return Shape.Real3;
            if (element == typeof(Vector4)) return Shape.Real4;
            if (element == typeof(Color)) return Shape.Color;
            if (element == typeof(Quaternion)) return Shape.Quat;
            if (element == typeof(Matrix4x4)) return Shape.Matrix4;
            if (element == typeof(Vector2Int)) return Shape.Int2;
            if (element == typeof(Vector3Int)) return Shape.Int3;
            return Shape.None;
        }

        // Precision of a real-valued code: 0 half, 1 float, 2 double; -1 when not real-valued.
        private static int Precision(UsdValueType type)
        {
            switch (type)
            {
                case UsdValueType.Half:
                case UsdValueType.Half2:
                case UsdValueType.Half3:
                case UsdValueType.Half4:
                case UsdValueType.Quath:
                    return 0;
                case UsdValueType.Float:
                case UsdValueType.Float2:
                case UsdValueType.Float3:
                case UsdValueType.Float4:
                case UsdValueType.Quatf:
                    return 1;
                case UsdValueType.Double:
                case UsdValueType.TimeCode:
                case UsdValueType.Double2:
                case UsdValueType.Double3:
                case UsdValueType.Double4:
                case UsdValueType.Quatd:
                case UsdValueType.Matrix2d:
                case UsdValueType.Matrix3d:
                case UsdValueType.Matrix4d:
                    return 2;
                default:
                    return -1;
            }
        }

        // Components per element of a real-valued code.
        private static int Components(UsdValueType type)
        {
            switch (type)
            {
                case UsdValueType.Half2:
                case UsdValueType.Float2:
                case UsdValueType.Double2:
                    return 2;
                case UsdValueType.Half3:
                case UsdValueType.Float3:
                case UsdValueType.Double3:
                    return 3;
                case UsdValueType.Half4:
                case UsdValueType.Float4:
                case UsdValueType.Double4:
                case UsdValueType.Quath:
                case UsdValueType.Quatf:
                case UsdValueType.Quatd:
                case UsdValueType.Matrix2d:
                    return 4;
                case UsdValueType.Matrix3d:
                    return 9;
                case UsdValueType.Matrix4d:
                    return 16;
                default:
                    return 1;
            }
        }

        // Whether a C# shape reads and writes a declared type.
        private static bool Accepts(Shape shape, UsdValueType type)
        {
            switch (shape)
            {
                case Shape.Bool: return type == UsdValueType.Bool;
                case Shape.UChar: return type == UsdValueType.UChar;
                case Shape.Int: return type == UsdValueType.Int;
                case Shape.UInt: return type == UsdValueType.UInt;
                case Shape.Int64: return type == UsdValueType.Int64;
                case Shape.UInt64: return type == UsdValueType.UInt64;
                case Shape.Real:
                    return type == UsdValueType.Half || type == UsdValueType.Float || type == UsdValueType.Double ||
                           type == UsdValueType.TimeCode;
                case Shape.Text: return type == UsdValueType.String || type == UsdValueType.Token || type == UsdValueType.Asset;
                case Shape.Real2: return type == UsdValueType.Half2 || type == UsdValueType.Float2 || type == UsdValueType.Double2;
                case Shape.Real3: return type == UsdValueType.Half3 || type == UsdValueType.Float3 || type == UsdValueType.Double3;
                case Shape.Real4: return type == UsdValueType.Half4 || type == UsdValueType.Float4 || type == UsdValueType.Double4;
                case Shape.Color: return Accepts(Shape.Real3, type) || Accepts(Shape.Real4, type);
                case Shape.Quat: return type == UsdValueType.Quath || type == UsdValueType.Quatf || type == UsdValueType.Quatd;
                case Shape.Matrix4: return type == UsdValueType.Matrix4d;
                case Shape.Int2: return type == UsdValueType.Int2;
                case Shape.Int3: return type == UsdValueType.Int3;
                default: return false;
            }
        }

        // The type a new attribute is made of when no type name is given.
        private static string PlainTypeName(Shape shape, Type element, bool array)
        {
            string name;
            switch (shape)
            {
                case Shape.Bool: name = "bool"; break;
                case Shape.UChar: name = "uchar"; break;
                case Shape.Int: name = "int"; break;
                case Shape.UInt: name = "uint"; break;
                case Shape.Int64: name = "int64"; break;
                case Shape.UInt64: name = "uint64"; break;
                case Shape.Real: name = element == typeof(double) ? "double" : "float"; break;
                case Shape.Text: name = "string"; break;
                case Shape.Real2: name = "float2"; break;
                case Shape.Real3: name = "float3"; break;
                case Shape.Real4: name = "float4"; break;
                case Shape.Color: name = "color3f"; break;
                case Shape.Quat: name = "quatf"; break;
                case Shape.Matrix4: name = "matrix4d"; break;
                case Shape.Int2: name = "int2"; break;
                case Shape.Int3: name = "int3"; break;
                default: return null;
            }

            return array ? name + "[]" : name;
        }

        // The value type a type name declares ("point3f[]" -> Float3, array), roles resolved; None
        // when the name is not one this API knows (the native side then judges it).
        internal static UsdValueType TypeOfName(string typeName, out bool array)
        {
            array = typeName.EndsWith("[]", StringComparison.Ordinal);
            string name = array ? typeName.Substring(0, typeName.Length - 2) : typeName;
            switch (name)
            {
                case "bool": return UsdValueType.Bool;
                case "uchar": return UsdValueType.UChar;
                case "int": return UsdValueType.Int;
                case "uint": return UsdValueType.UInt;
                case "int64": return UsdValueType.Int64;
                case "uint64": return UsdValueType.UInt64;
                case "half": return UsdValueType.Half;
                case "float": return UsdValueType.Float;
                case "double": return UsdValueType.Double;
                case "timecode": return UsdValueType.TimeCode;
                case "string": return UsdValueType.String;
                case "token": return UsdValueType.Token;
                case "asset": return UsdValueType.Asset;
                case "int2": return UsdValueType.Int2;
                case "int3": return UsdValueType.Int3;
                case "int4": return UsdValueType.Int4;
                case "quath": return UsdValueType.Quath;
                case "quatf": return UsdValueType.Quatf;
                case "quatd": return UsdValueType.Quatd;
                case "matrix2d": return UsdValueType.Matrix2d;
                case "matrix3d": return UsdValueType.Matrix3d;
                case "matrix4d":
                case "frame4d": return UsdValueType.Matrix4d;
            }

            // half2 ... double4 and the roles: point3f, normal3d, vector3h, color4f, texCoord2f, ...
            foreach (string prefix in new[] { "half", "float", "double", "point", "normal", "vector", "color", "texCoord" })
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                string rest = name.Substring(prefix.Length);
                bool role = prefix != "half" && prefix != "float" && prefix != "double";
                if (!role)
                {
                    // halfN, floatN, doubleN
                    if (rest.Length == 1 && rest[0] >= '2' && rest[0] <= '4')
                    {
                        int count = rest[0] - '0';
                        int precision = prefix == "half" ? 0 : prefix == "float" ? 1 : 2;
                        return VectorType(count, precision);
                    }

                    continue;
                }

                if (rest.Length == 2 && rest[0] >= '2' && rest[0] <= '4' && (rest[1] == 'h' || rest[1] == 'f' || rest[1] == 'd'))
                {
                    return VectorType(rest[0] - '0', rest[1] == 'h' ? 0 : rest[1] == 'f' ? 1 : 2);
                }
            }

            return UsdValueType.None;
        }

        private static UsdValueType VectorType(int count, int precision) =>
            (UsdValueType)((int)UsdValueType.Half2 + (precision * 3) + (count - 2));

        internal static bool TryRead(UsdAttribute attribute, Type element, bool array, UsdTimeCode time, out object value)
        {
            Shape shape = ShapeOfType(element);
            if (shape == Shape.None)
            {
                throw new ArgumentException($"{element.Name} is not a type UsdAttribute reads; see its documentation.");
            }

            UsdStage stage = attribute.Stage;
            lock (stage.Gate)
            {
                IntPtr context = stage.Context;
                RUsdAttributeInfo info = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                if (info.Exists == 0)
                {
                    throw new UsdStageException($"{attribute.Prim.Path} has no attribute '{attribute.Name}'.");
                }

                var type = (UsdValueType)(info.ValueType & 0xff);
                bool isArray = (info.ValueType & UsdNative.ValueArray) != 0;
                if (!Accepts(shape, type) || isArray != array)
                {
                    throw new UsdStageException(
                        $"{attribute.Path} holds {Describe(type, isArray)}, which does not read as {element.Name}{(array ? "[]" : string.Empty)}.");
                }

                int code = (int)type | (isArray ? UsdNative.ValueArray : 0);
                if (!Measure(context, attribute, code, time, out long count, out long bytes))
                {
                    value = null;
                    return false;
                }

                Array decoded = Decode(context, attribute, code, time, shape, element, type, count, bytes);
                value = array ? decoded : decoded.GetValue(0);
                return true;
            }
        }

        internal static double[] ReadComponents(UsdAttribute attribute, UsdTimeCode time)
        {
            UsdStage stage = attribute.Stage;
            lock (stage.Gate)
            {
                IntPtr context = stage.Context;
                RUsdAttributeInfo info = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                var type = (UsdValueType)(info.ValueType & 0xff);
                if (info.Exists == 0 || Precision(type) < 0)
                {
                    throw new UsdStageException($"{attribute.Path} is not a real-valued attribute.");
                }

                int code = info.ValueType;
                if (!Measure(context, attribute, code, time, out _, out long bytes))
                {
                    return null;
                }

                if (Precision(type) == 2)
                {
                    return ReadWire<double>(context, attribute, code, time, bytes);
                }

                float[] floats = ReadWire<float>(context, attribute, code, time, bytes);
                var components = new double[floats.Length];
                for (int i = 0; i < floats.Length; i++)
                {
                    components[i] = floats[i];
                }

                return components;
            }
        }

        internal static string[] ReadResolved(UsdAttribute attribute, UsdTimeCode time, bool array)
        {
            UsdStage stage = attribute.Stage;
            lock (stage.Gate)
            {
                IntPtr context = stage.Context;
                RUsdAttributeInfo info = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                bool isArray = (info.ValueType & UsdNative.ValueArray) != 0;
                if (info.Exists == 0 || (info.ValueType & 0xff) != (int)UsdValueType.Asset || isArray != array)
                {
                    throw new UsdStageException($"{attribute.Path} is not an asset{(array ? " array" : string.Empty)} attribute.");
                }

                int code = info.ValueType | UsdNative.ValueResolved;
                if (!Measure(context, attribute, code, time, out _, out long bytes))
                {
                    return null;
                }

                return SplitStrings(ReadWire<byte>(context, attribute, code, time, bytes));
            }
        }

        internal static void Write(UsdAttribute attribute, object value, Type element, bool array, UsdTimeCode time,
                                   string typeName, int flags)
        {
            if (time.IsEarliestTime)
            {
                throw new ArgumentException("A value is written at the default or at a time code, not at the earliest time.");
            }

            UsdStage stage = attribute.Stage;
            lock (stage.Gate)
            {
                stage.EnsureWritable();
                IntPtr context = stage.Context;
                if ((flags & (UsdNative.SetBlock | UsdNative.SetClear)) != 0)
                {
                    RUsdAttributeInfo existing = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                    if (existing.Exists == 0)
                    {
                        throw new UsdStageException($"{attribute.Prim.Path} has no attribute '{attribute.Name}'.");
                    }

                    UsdNative.WriteAttributeValue(context, attribute.Prim.Path, attribute.Name, null, flags, time.Kind, time.Value,
                                                  existing.ValueType == 0 ? (int)UsdValueType.Float : existing.ValueType, null, 1, 0);
                    return;
                }

                Shape shape = ShapeOfType(element);
                if (shape == Shape.None)
                {
                    throw new ArgumentException($"{element.Name} is not a type UsdAttribute writes; see its documentation.");
                }

                RUsdAttributeInfo info = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                UsdValueType type;
                bool isArray;
                if (info.Exists != 0)
                {
                    type = (UsdValueType)(info.ValueType & 0xff);
                    isArray = (info.ValueType & UsdNative.ValueArray) != 0;
                    typeName = null;
                }
                else
                {
                    typeName = typeName ?? PlainTypeName(shape, element, array);
                    type = TypeOfName(typeName, out isArray);
                    if (type == UsdValueType.None)
                    {
                        throw new UsdStageException($"'{typeName}' is not a value type this API writes.");
                    }
                }

                if (!Accepts(shape, type) || isArray != array)
                {
                    throw new UsdStageException(
                        $"{attribute.Path} {(info.Exists != 0 ? "holds" : "would be")} {Describe(type, isArray)}, which {element.Name}{(array ? "[]" : string.Empty)} does not write.");
                }

                Array items = array ? (Array)value : Single(element, value);
                Array wire = Encode(items, shape, type, out long count);
                long bytes = WireBytes(wire);
                int code = (int)type | (isArray ? UsdNative.ValueArray : 0);
                UsdNative.WriteAttributeValue(context, attribute.Prim.Path, attribute.Name, typeName, 0, time.Kind, time.Value, code,
                                              wire, array ? count : 1, bytes);
            }
        }

        internal static void WriteComponents(UsdAttribute attribute, double[] components, UsdTimeCode time)
        {
            UsdStage stage = attribute.Stage;
            lock (stage.Gate)
            {
                stage.EnsureWritable();
                IntPtr context = stage.Context;
                RUsdAttributeInfo info = UsdNative.GetAttributeInfo(context, attribute.Prim.Path, attribute.Name);
                var type = (UsdValueType)(info.ValueType & 0xff);
                bool isArray = (info.ValueType & UsdNative.ValueArray) != 0;
                if (info.Exists == 0 || Precision(type) < 0)
                {
                    throw new UsdStageException($"{attribute.Path} is not an existing real-valued attribute.");
                }

                int per = Components(type);
                if (components.Length % per != 0 || (!isArray && components.Length != per))
                {
                    throw new ArgumentException($"{attribute.Path} takes {per} components per element.");
                }

                Array wire;
                if (Precision(type) == 2)
                {
                    wire = (double[])components.Clone();
                }
                else
                {
                    var floats = new float[components.Length];
                    for (int i = 0; i < floats.Length; i++)
                    {
                        floats[i] = (float)components[i];
                    }

                    wire = floats;
                }

                UsdNative.WriteAttributeValue(context, attribute.Prim.Path, attribute.Name, null, 0, time.Kind, time.Value,
                                              info.ValueType, wire, isArray ? components.Length / per : 1, WireBytes(wire));
            }
        }

        // ---- Reading ----

        private static bool Measure(IntPtr context, UsdAttribute attribute, int code, UsdTimeCode time, out long count, out long bytes) =>
            UsdNative.MeasureAttributeValue(context, attribute.Prim.Path, attribute.Name, time.Kind, time.Value, code, out count,
                                            out bytes);

        // The measured value as an array of W, its wire element. Under the stage's Gate, right after Measure.
        private static W[] ReadWire<W>(IntPtr context, UsdAttribute attribute, int code, UsdTimeCode time, long bytes) where W : struct
        {
            int size = Marshal.SizeOf<W>();
            if (bytes % size != 0 || bytes / size > int.MaxValue)
            {
                throw new UsdStageException($"{attribute.Path} is too large, or not a whole number of elements, to read.");
            }

            var items = new W[bytes / size];
            if (items.Length > 0)
            {
                UsdNative.ReadAttributeValue(context, attribute.Prim.Path, attribute.Name, time.Kind, time.Value, code, items, bytes);
            }

            return items;
        }

        private static Array Decode(IntPtr context, UsdAttribute attribute, int code, UsdTimeCode time, Shape shape, Type element,
                                    UsdValueType type, long count, long bytes)
        {
            int precision = Precision(type);
            switch (shape)
            {
                case Shape.Bool:
                {
                    byte[] raw = ReadWire<byte>(context, attribute, code, time, bytes);
                    var values = new bool[raw.Length];
                    for (int i = 0; i < raw.Length; i++)
                    {
                        values[i] = raw[i] != 0;
                    }

                    return values;
                }
                case Shape.UChar: return ReadWire<byte>(context, attribute, code, time, bytes);
                case Shape.Int: return ReadWire<int>(context, attribute, code, time, bytes);
                case Shape.UInt: return ReadWire<uint>(context, attribute, code, time, bytes);
                case Shape.Int64: return ReadWire<long>(context, attribute, code, time, bytes);
                case Shape.UInt64: return ReadWire<ulong>(context, attribute, code, time, bytes);
                case Shape.Int2: return ReadWire<Vector2Int>(context, attribute, code, time, bytes);
                case Shape.Int3: return ReadWire<Vector3Int>(context, attribute, code, time, bytes);
                case Shape.Text: return SplitStrings(ReadWire<byte>(context, attribute, code, time, bytes));
            }

            // Real-valued: read straight into the Unity type when it is the wire layout (float
            // precision), else through the components.
            if (precision < 2)
            {
                switch (shape)
                {
                    case Shape.Real2: return ReadWire<Vector2>(context, attribute, code, time, bytes);
                    case Shape.Real3: return ReadWire<Vector3>(context, attribute, code, time, bytes);
                    case Shape.Real4: return ReadWire<Vector4>(context, attribute, code, time, bytes);
                    case Shape.Quat: return ReadWire<Quaternion>(context, attribute, code, time, bytes);
                    case Shape.Color when Components(type) == 4: return ReadWire<Color>(context, attribute, code, time, bytes);
                    case Shape.Real when element == typeof(float): return ReadWire<float>(context, attribute, code, time, bytes);
                }
            }
            else if (shape == Shape.Real && element == typeof(double))
            {
                return ReadWire<double>(context, attribute, code, time, bytes);
            }

            double[] c = precision == 2
                ? ReadWire<double>(context, attribute, code, time, bytes)
                : Widen(ReadWire<float>(context, attribute, code, time, bytes));
            int per = Components(type);
            int n = c.Length / per;
            switch (shape)
            {
                case Shape.Real when element == typeof(float):
                {
                    var values = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = (float)c[i];
                    }

                    return values;
                }
                case Shape.Real:
                    return c;
                case Shape.Real2:
                {
                    var values = new Vector2[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = new Vector2((float)c[i * 2], (float)c[i * 2 + 1]);
                    }

                    return values;
                }
                case Shape.Real3:
                {
                    var values = new Vector3[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = new Vector3((float)c[i * 3], (float)c[i * 3 + 1], (float)c[i * 3 + 2]);
                    }

                    return values;
                }
                case Shape.Real4:
                {
                    var values = new Vector4[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = new Vector4((float)c[i * 4], (float)c[i * 4 + 1], (float)c[i * 4 + 2], (float)c[i * 4 + 3]);
                    }

                    return values;
                }
                case Shape.Color:
                {
                    var values = new Color[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = per == 4
                            ? new Color((float)c[i * 4], (float)c[i * 4 + 1], (float)c[i * 4 + 2], (float)c[i * 4 + 3])
                            : new Color((float)c[i * 3], (float)c[i * 3 + 1], (float)c[i * 3 + 2], 1f);
                    }

                    return values;
                }
                case Shape.Quat:
                {
                    var values = new Quaternion[n];
                    for (int i = 0; i < n; i++)
                    {
                        values[i] = new Quaternion((float)c[i * 4], (float)c[i * 4 + 1], (float)c[i * 4 + 2], (float)c[i * 4 + 3]);
                    }

                    return values;
                }
                case Shape.Matrix4:
                {
                    var values = new Matrix4x4[n];
                    for (int i = 0; i < n; i++)
                    {
                        var m = new Matrix4x4();
                        for (int row = 0; row < 4; row++)
                        {
                            for (int column = 0; column < 4; column++)
                            {
                                // USD row r, column k is Unity's row k, column r.
                                m[column, row] = (float)c[i * 16 + row * 4 + column];
                            }
                        }

                        values[i] = m;
                    }

                    return values;
                }
            }

            throw new UsdStageException($"{attribute.Path} cannot be read as {element.Name}.");
        }

        private static double[] Widen(float[] floats)
        {
            var values = new double[floats.Length];
            for (int i = 0; i < floats.Length; i++)
            {
                values[i] = floats[i];
            }

            return values;
        }

        private static string[] SplitStrings(byte[] bytes)
        {
            var items = new List<string>();
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    items.Add(Encoding.UTF8.GetString(bytes, start, i - start));
                    start = i + 1;
                }
            }

            return items.ToArray();
        }

        // ---- Writing ----

        private static Array Single(Type element, object value)
        {
            Array items = Array.CreateInstance(element, 1);
            items.SetValue(value, 0);
            return items;
        }

        private static long WireBytes(Array wire) =>
            wire is byte[] raw ? raw.LongLength : wire.LongLength * Marshal.SizeOf(wire.GetType().GetElementType());

        // The wire array for `items` written as `type`, and its element count.
        private static Array Encode(Array items, Shape shape, UsdValueType type, out long count)
        {
            count = items.LongLength;
            int precision = Precision(type);
            switch (shape)
            {
                case Shape.Bool:
                {
                    var raw = new byte[items.Length];
                    var values = (bool[])items;
                    for (int i = 0; i < raw.Length; i++)
                    {
                        raw[i] = values[i] ? (byte)1 : (byte)0;
                    }

                    return raw;
                }
                case Shape.UChar:
                case Shape.Int:
                case Shape.UInt:
                case Shape.Int64:
                case Shape.UInt64:
                case Shape.Int2:
                case Shape.Int3:
                    return items;
                case Shape.Text:
                {
                    var text = new StringBuilder();
                    foreach (string item in (string[])items)
                    {
                        if (item != null && item.IndexOf('\0') >= 0)
                        {
                            throw new ArgumentException("A USD string cannot hold a NUL character.");
                        }

                        text.Append(item ?? string.Empty).Append('\0');
                    }

                    return Encoding.UTF8.GetBytes(text.ToString());
                }
            }

            // Real-valued: the Unity array is the wire layout at float precision of the same width.
            int per = Components(type);
            bool floats = precision < 2;
            if (floats)
            {
                switch (shape)
                {
                    case Shape.Real2 when items is Vector2[]:
                    case Shape.Real3 when items is Vector3[]:
                    case Shape.Real4 when items is Vector4[]:
                    case Shape.Quat when items is Quaternion[]:
                    case Shape.Real when items is float[]:
                        return items;
                    case Shape.Color when per == 4:
                        return items;
                }
            }
            else if (shape == Shape.Real && items is double[])
            {
                return items;
            }

            var c = new double[items.Length * per];
            for (int i = 0; i < items.Length; i++)
            {
                object item = items.GetValue(i);
                switch (item)
                {
                    case float f: c[i] = f; break;
                    case double d: c[i] = d; break;
                    case Vector2 v: c[i * 2] = v.x; c[i * 2 + 1] = v.y; break;
                    case Vector3 v: c[i * 3] = v.x; c[i * 3 + 1] = v.y; c[i * 3 + 2] = v.z; break;
                    case Vector4 v: c[i * 4] = v.x; c[i * 4 + 1] = v.y; c[i * 4 + 2] = v.z; c[i * 4 + 3] = v.w; break;
                    case Quaternion q: c[i * 4] = q.x; c[i * 4 + 1] = q.y; c[i * 4 + 2] = q.z; c[i * 4 + 3] = q.w; break;
                    case Color color when per == 3: c[i * 3] = color.r; c[i * 3 + 1] = color.g; c[i * 3 + 2] = color.b; break;
                    case Color color: c[i * 4] = color.r; c[i * 4 + 1] = color.g; c[i * 4 + 2] = color.b; c[i * 4 + 3] = color.a; break;
                    case Matrix4x4 m:
                        for (int row = 0; row < 4; row++)
                        {
                            for (int column = 0; column < 4; column++)
                            {
                                c[i * 16 + row * 4 + column] = m[column, row];
                            }
                        }

                        break;
                }
            }

            if (!floats)
            {
                return c;
            }

            var narrow = new float[c.Length];
            for (int i = 0; i < c.Length; i++)
            {
                narrow[i] = (float)c[i];
            }

            return narrow;
        }

        private static string Describe(UsdValueType type, bool array) =>
            (type == UsdValueType.None ? "a type this API has no form for" : type.ToString()) + (array ? "[]" : string.Empty);
    }
}
