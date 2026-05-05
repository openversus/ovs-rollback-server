using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;


// Anything that would cause this is guarded by checking IntPtr.Size or UIntPtr.Size
// But the compiler just won't stfu about it
#pragma warning disable CS8778 // Constant value may overflow at runtime (use 'unchecked' syntax to override)

namespace OVS.Rollback.Common
{
    /// <summary>
    /// Provides global static properties, constants, and some related utility methods for program-wide configuration, logging,
    /// ANSI color codes, default values, etc.
    /// </summary>
    /// <remarks>The Statics class centralizes access to shared resources and configuration settings used
    /// throughout the application, such as logging color schemes, date and time formats, process privilege information,
    /// and ANSI color utilities. It also exposes helpers for dependency injection. This class is intended for use
    /// as a static utility and cannot be instantiated.</remarks>
    public static class Statics
    {
        private static LoggingColorRoot _loggingColorRoot = new LoggingColorRoot();
        private static string _dateFormat = "yyyy-MM-dd";
        private static string _timeFormat = "HH:mm:ss.fff";
        internal static string PrematchMatchUpdateKey = String.Empty;
        public static DIContainer? _diContainer;

        /// <summary>
        /// Gets the application's dependency injection container instance.
        /// </summary>
        /// <remarks>Use this property to access registered services and resolve dependencies throughout
        /// the program. The property may return null if the container has not been initialized, but that
        /// should never happen because of the ModuleInitializer in the class.
        /// </remarks>
        public static DIContainer? DIContainer
        {
            get => _diContainer;
        }

        /// <summary>
        /// Gets the current color configuration used for logging output. (unimplemented)
        /// </summary>
        /// <remarks>If no color configuration has been set, a default instance is returned. The returned
        /// configuration determines how log messages are colorized in supported outputs.
        /// 
        /// This is currently unimplemented and Statics.ANSI color codes are used directly in logging.
        /// </remarks>
        public static LoggingColorRoot ColorConfig
        {
            get => _loggingColorRoot ?? new LoggingColorRoot();
        }

        /// <summary>
        /// Gets the largest representable value of type <see langword="nint"/> for the current platform.
        /// </summary>
        /// <remarks>On 64-bit platforms, this value is equal to Int64.MaxValue. On 32-bit platforms, it
        /// is equal to Int32.MaxValue. Use this property to determine the maximum value that can be safely stored in a
        /// native-sized integer on the executing platform without having to if/else every single fucking time.</remarks>
        public static nint INT_CEILING
        {
            get
            {
                if (IntPtr.Size >= 8)
                {
                    unchecked
                    {
                        return (nint)Int64.MaxValue;
                    }

                }
                else
                {
                    return Int32.MaxValue;
                }
            }
        }

        /// <summary>
        /// Gets the smallest possible value of a native-sized integer for the current platform.
        /// </summary>
        /// <remarks>On 64-bit platforms, this value is equal to <see cref="Int64.MinValue"/>. On 32-bit
        /// platforms, it is equal to <see cref="Int32.MinValue"/>. Use this property to determine the minimum value
        /// that can be represented by a <see langword="nint"/> on the executing platform.</remarks>
        public static nint INT_FLOOR
        {
            get
            {
                if (IntPtr.Size >= 8)
                {
                    unchecked
                    {
                        return (nint)Int64.MinValue;
                    }

                }
                else
                {
                    return Int32.MinValue;
                }
            }
        }

        /// <summary>
        /// Gets the maximum value that can be represented by an unsigned native-sized integer on the current platform.
        /// </summary>
        /// <remarks>The value of this property depends on the size of a native unsigned integer: it is
        /// UInt64.MaxValue on 64-bit platforms and UInt32.MaxValue on 32-bit platforms.</remarks>
        public static nuint UINT_CEILING
        {
            get
            {
                if (UIntPtr.Size >= 8)
                {
                    unchecked
                    {
                        return (nuint)UInt64.MaxValue;
                    }

                }
                else
                {
                    return UInt32.MaxValue;
                }
            }
        }

        /// <summary>
        /// Gets the smallest possible value of an unsigned native-sized integer.
        /// </summary>
        public static nuint UINT_FLOOR { get => 0; }

        /// <summary>
        /// Gets or sets the date format string used for date representations in logs (unimplemented).
        /// </summary>
        /// <remarks>If not explicitly set, the default format is "yyyy-MM-dd". The format string should
        /// follow standard .NET date and time format patterns.</remarks>
        public static string DateFormat
        {
            get
            {
                if (_dateFormat.StringIsNullOrWhiteSpace)
                {
                    _dateFormat = "yyyy-MM-dd";
                    return _dateFormat;
                }
                else
                {
                    return _dateFormat;
                }
            }
            set => _dateFormat = value;
        }

        /// <summary>
        /// Gets or sets the format string used to represent time values. (unimplemented)
        /// </summary>
        /// <remarks>If not explicitly set, the default format is "HH:mm:ss". The format string should
        /// follow standard .NET date and time format patterns. Changing this property affects how time values are
        /// displayed throughout the application.</remarks>
        public static string TimeFormat
        {
            get
            {
                if (_timeFormat.StringIsNullOrWhiteSpace)
                {
                    //_timeFormat = "HH:mm:ss.fffK";
                    _timeFormat = "HH:mm:ss.fff";
                    return _timeFormat;
                }
                else
                {
                    return _timeFormat;
                }
            }
            set => _timeFormat = value;
        }

        /// <summary>
        /// Gets a mapping of log levels to their associated color codes for display purposes. (unimplemented)
        /// </summary>
        /// <remarks>The returned dictionary provides a color code string for each defined log level. This
        /// can be used to format log output with level-specific colors. The
        /// color codes are determined by the current configuration in the <see cref="ColorConfig"/> class.</remarks>
        public static Dictionary<LogLevel, string> LogLevelColors
        {
            get
            {
                return new Dictionary<LogLevel, string> {
                    { LogLevel.Trace, $"[{ColorConfig.LevelTrace}]" },
                    { LogLevel.Debug, $"[{ColorConfig.LevelDebug}]" },
                    { LogLevel.Information, $"[{ColorConfig.LevelInformation}]" },
                    { LogLevel.Warning, $"[{ColorConfig.LevelWarning}]" },
                    { LogLevel.Error, $"[{ColorConfig.LevelError}]" },
                    { LogLevel.Critical, $"[{ColorConfig.LevelCritical}]" }
                };
            }
        }

        /// <summary>
        /// Holds static string constants used throughout the program. Currently only contains regular expression patterns.
        /// </summary>
        /// <remarks>This class contains only static string fields intended for replacement/interpolation through the program.
        /// The regular expression patterns are used to match specific input formats related to custom markup parsing within
        /// the application.</remarks>
        internal static class StaticStrings
        {
            //internal static readonly string PseudoMarkupRegex = "(?<markup>({_|{_\\s*?)(?<tag>(\\w+|\\w+\\s*?\\w+\\s*?\\w+\\s*?\\w+))(\\s*?_}|_})|(?<closetag>{_}))";
            internal static readonly string PseudoMarkupRegex = "(?<tag>({_\\s*?\\w+\\s*?\\w+\\s*?\\w+\\s*?\\w+\\s*?_}?))|(?<closetag>{\\s*?_\\s*?})";
            //internal static readonly string PseudoMarkupRegex = @"(?<tag>{_\s*\w+(?:\s+\w+){0,4}\s*_})|(?<closetag>{_})";
        }

        /// <summary>
        /// Provides utility types and methods for working with common numeric types and their metadata.
        /// </summary>
        /// <remarks>This class contains nested types and members that expose information about
        /// standard integer types, including their names, value ranges, signedness, and bit widths. It is intended to
        /// assist with reflection, type analysis, or generic numeric operations where type metadata is
        /// required.
        /// 
        /// <br><br></br></br>I got <b><i>REAL fuckin' tired</i></b> of dealing with numeric types' idiosyncrasies while
        /// trying to remain architecture-sensitive/architecture-agnostic when writing the interactive mode helper
        /// class, and at one point I snapped and rage-coded this monstrosity. You're welcome and I'm sorry.
        /// </remarks>
        public static class FuckinNumbers
        {
            /// <summary>
            /// Provides static metadata and utility methods for common integral numeric types, including signed and
            /// unsigned integer types of various sizes.
            /// </summary>
            /// <remarks>The Types struct exposes a series of static anonymous objects containing metadata about each
            /// supported numeric type, such as its name, .NET type, minimum and maximum values, signedness, bit
            /// width, and an actual initialized instance of the type. It also provides arrays grouping all, signed,
            /// and unsigned types, as well as a method to retrieve type metadata by System.Type. This struct is 
            /// intended to simplify reflection and type analysis scenarios where consistent access to
            /// numeric type characteristics is required.</remarks>
            public struct Types
            {
                /// <summary>
                /// A lambda function that returns an instance of the specified value Type when invoked. Defined this way to get around
                /// the fact that the compiler won't allow me to explicitly/directly define it as a field in an anonymous object.
                /// </summary>
                /// <typeparam name="T">The type of the value to be returned by the generated function.</typeparam>
                /// <param name="value">The value to be captured and returned by the generated function.</param>
                /// <returns>A function that, when called, returns an initialized instance of the specified .NET Type.</returns>
                private static Func<T> ImUglyButMakePrettyNumbers<T>(T value) => () => value;

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 16-bit signed <see cref="short"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object Short = new
                {
                    Name = "short",
                    Type = typeof(short),
                    MinValue = short.MinValue,
                    MaxValue = short.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = true,
                    Is32Bit = false,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers((short)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 16-bit unsigned <see cref="ushort"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object UShort = new
                {
                    Name = "ushort",
                    Type = typeof(ushort),
                    MinValue = ushort.MinValue,
                    MaxValue = ushort.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = true,
                    Is32Bit = false,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers((ushort)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit signed <see cref="int"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object Int = new
                {
                    Name = "int",
                    Type = typeof(int),
                    MinValue = int.MinValue,
                    MaxValue = int.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = true,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers(1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit unsigned <see cref="uint"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object UInt = new
                {
                    Name = "uint",
                    Type = typeof(uint),
                    MinValue = uint.MinValue,
                    MaxValue = uint.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = true,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers(1u)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 64-bit signed <see cref="long"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object Long = new
                {
                    Name = "long",
                    Type = typeof(long),
                    MinValue = long.MinValue,
                    MaxValue = long.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = false,
                    Is64Bit = true,
                    InstanceOf = ImUglyButMakePrettyNumbers(1L)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 64-bit unsigned <see cref="ulong"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object ULong = new
                {
                    Name = "ulong",
                    Type = typeof(ulong),
                    MinValue = ulong.MinValue,
                    MaxValue = ulong.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = false,
                    Is64Bit = true,
                    InstanceOf = ImUglyButMakePrettyNumbers(1UL)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the signed architecture-specific
                /// <see langword="nint"/> numeric type. This will dynamically resolve to a 32-bit or 64-bit signed integer
                /// depending on the platform architecture.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object NInt = new
                {
                    Name = "nint",
                    Type = typeof(nint),

                    MinValue = System.IntPtr.Size >= 8 ? (nint)long.MinValue : int.MinValue,
                    MaxValue = System.IntPtr.Size >= 8 ? (nint)long.MaxValue : int.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = System.IntPtr.Size == 4,
                    Is64Bit = System.IntPtr.Size == 8,
                    InstanceOf = ImUglyButMakePrettyNumbers((nint)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the signed architecture-specific
                /// <see langword="nuint"/> numeric type. This will dynamically resolve to a 32-bit or 64-bit unsigned integer
                /// depending on the platform architecture. This is a <b>huge</b> number which <i>might</i> be able to represent your mom's weight.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object NUInt = new
                {
                    Name = "nuint",
                    Type = typeof(nuint),
                    MinValue = (nuint)0,
                    MaxValue = System.UIntPtr.Size == 8 ? (nuint)ulong.MaxValue : uint.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = System.UIntPtr.Size == 4,
                    Is64Bit = System.UIntPtr.Size == 8,
                    InstanceOf = ImUglyButMakePrettyNumbers((nuint)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit signed <see cref="System.Int32"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object Int32 = new
                {
                    Name = "Int32",
                    Type = typeof(Int32),
                    MinValue = int.MinValue,
                    MaxValue = int.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = true,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers((Int32)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit unsigned <see cref="System.UInt32"/> numeric type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object UInt32 = new
                {
                    Name = "UInt32",
                    Type = typeof(UInt32),
                    MinValue = System.UInt32.MinValue,
                    MaxValue = System.UInt32.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = true,
                    Is64Bit = false,
                    InstanceOf = ImUglyButMakePrettyNumbers((UInt32)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit signed <see langword="Int64"/> numeric type.
                /// This is a language alias to the <see langword="long"/> Type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object Int64 = new
                {
                    Name = "Int64",
                    Type = typeof(Int64),
                    MinValue = long.MinValue,
                    MaxValue = long.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = false,
                    Is64Bit = true,
                    InstanceOf = ImUglyButMakePrettyNumbers((Int64)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the 32-bit signed <see langword="UInt64"/> numeric type.
                /// This is a language alias to the <see langword="ulong"/> Type.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object UInt64 = new
                {
                    Name = "UInt64",
                    Type = typeof(UInt64),
                    MinValue = System.UInt64.MinValue,
                    MaxValue = System.UInt64.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = false,
                    Is64Bit = true,
                    InstanceOf = ImUglyButMakePrettyNumbers((UInt64)1)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the signed architecture-specific
                /// <see cref="System.IntPtr"/> numeric type. This will dynamically resolve to a pointer of a representation of
                /// a 32-bit or 64-bit signed integer depending on the platform architecture.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object IntPtr = new
                {
                    Name = "IntPtr",
                    Type = typeof(IntPtr),
                    MinValue = System.IntPtr.Size == 8 ? (nint)long.MinValue : int.MinValue,
                    MaxValue = System.IntPtr.Size == 8 ? (nint)long.MaxValue : int.MaxValue,
                    Signed = true,
                    Unsigned = false,
                    IsNegativeOK = true,
                    Is16Bit = false,
                    Is32Bit = System.IntPtr.Size == 4,
                    Is64Bit = System.IntPtr.Size == 8,
                    InstanceOf = ImUglyButMakePrettyNumbers(System.IntPtr.Zero)
                };

                /// <summary>
                /// An anonymous object providing metadata and utility information about the signed architecture-specific
                /// <see cref="System.UIntPtr"/> numeric type. This will dynamically resolve to a pointer of a representation of
                /// a 32-bit or 64-bit unsigned integer depending on the platform architecture.
                /// </summary>
                /// <remarks>This object includes details such as the type name, minimum and
                /// maximum values, bitness, and whether the type is signed or unsigned. It can be used to
                /// programmatically access type characteristics about this Type in scenarios such as type reflection,
                /// validation, or dynamic type handling.</remarks>
                public static readonly object UIntPtr = new
                {
                    Name = "UIntPtr",
                    Type = typeof(UIntPtr),
                    MinValue = System.UIntPtr.Zero,
                    MaxValue = System.UIntPtr.Size == 8 ? (nuint)ulong.MaxValue : uint.MaxValue,
                    Signed = false,
                    Unsigned = true,
                    IsNegativeOK = false,
                    Is16Bit = false,
                    Is32Bit = System.UIntPtr.Size == 4,
                    Is64Bit = System.UIntPtr.Size == 8,
                    InstanceOf = ImUglyButMakePrettyNumbers(System.UIntPtr.Zero)
                };

                /// <summary>
                /// Provides an array containing all supported integral and pointer-related numeric types.
                /// </summary>
                /// <remarks>The array includes both signed and unsigned integer types, as well as
                /// platform-specific pointer types such as <see cref="System.IntPtr"/> and <see cref="System.UIntPtr"/>. This array can
                /// be used to perform type checks or filtering operations involving these numeric types.</remarks>
                public static readonly Type[] AllTypesArray = [
                    typeof(short),
                    typeof(int),
                    typeof(long),
                    typeof(Int32),
                    typeof(Int64),
                    typeof(ushort),
                    typeof(uint),
                    typeof(nuint),
                    typeof(ulong),
                    typeof(UInt32),
                    typeof(UInt64),
                    typeof(IntPtr),
                    typeof(UIntPtr)
                    ];

                /// <summary>
                /// Represents an array containing the .NET types for signed integer values.
                /// </summary>
                /// <remarks>The array includes types such as <see cref="System.Int16"/>, <see
                /// cref="System.Int32"/>, <see cref="System.Int64"/>, <see cref="System.IntPtr"/>, and their aliases.
                /// This can be used to identify or filter signed integer types in reflection or type-checking
                /// scenarios.</remarks>
                public static Type[] SignedTypesArray = [
                    typeof(short),
                    typeof(int),
                    typeof(long),
                    typeof(nint),
                    typeof(Int64),
                    typeof(IntPtr)
                    ];

                /// <summary>
                /// Provides an array containing the .NET types that represent unsigned integer values.
                /// </summary>
                /// <remarks>The array includes common unsigned types such as <see cref="ushort"/>, <see cref="uint"/>, <see langword="nuint"/>,
                /// <see cref="ulong"/>, etc. This can be used to check or filter types that are unsigned numeric
                /// types in .NET.</remarks>
                public static Type[] UnsignedTypesArray = [
                    typeof(ushort),
                    typeof(uint),
                    typeof(nuint),
                    typeof(ulong),
                    typeof(UInt64),
                    typeof(UIntPtr)
                    ];

                /// <summary>
                /// Retrieves type-specific metadata or information for the specified integral or pointer type.
                /// </summary>
                /// <remarks>Supported types include short, ushort, int, uint, long, ulong, nint,
                /// nuint, Int32, UInt32, Int64, UInt64, IntPtr, and UIntPtr. For unsupported types, the method returns
                /// null.</remarks>
                /// <param name="type">The type for which to obtain metadata. Must be an integral or pointer type such as short, int, long,
                /// IntPtr, or their unsigned or platform-specific variants.</param>
                /// <returns>An object containing metadata or information associated with the specified type, or null if the type
                /// is not supported.</returns>
                public static object? GetTypeInfo(Type type)
                {
                    if (type == typeof(short)) return Short;
                    if (type == typeof(ushort)) return UShort;
                    if (type == typeof(int)) return Int;
                    if (type == typeof(uint)) return UInt;
                    if (type == typeof(long)) return Long;
                    if (type == typeof(ulong)) return ULong;
                    if (type == typeof(nint)) return NInt;
                    if (type == typeof(nuint)) return NUInt;
                    if (type == typeof(Int32)) return Int32;
                    if (type == typeof(UInt32)) return UInt32;
                    if (type == typeof(Int64)) return Int64;
                    if (type == typeof(UInt64)) return UInt64;
                    if (type == typeof(IntPtr)) return IntPtr;
                    if (type == typeof(UIntPtr)) return UIntPtr;
                    return null;
                }
            }

        }

        /// <summary>
        /// Provides utility methods for log level formatting and dependency resolution within the program's
        /// internal infrastructure.
        /// </summary>
        /// <remarks>This class is intended for internal use and exposes static methods to assist with log
        /// level abbreviation (unimplemented) and service retrieval from the program's DI container. It is not
        /// designed for direct use by external consumers.</remarks>
        internal static class Functions
        {
            public static string GetShortLogLevel(LogLevel logLevel)
            {
                return logLevel switch
                {
                    LogLevel.Trace => "TRC",
                    LogLevel.Debug => "DBG",
                    LogLevel.Information => "NFO",
                    LogLevel.Warning => "WRN",
                    LogLevel.Error => "ERR",
                    LogLevel.Critical => "CRT",
                    _ => "white"
                };
            }
        }

        /// <summary>
        /// Provides ANSI escape codes and helper methods for formatting console output with colors and text styles.
        /// </summary>
        /// <remarks>The ANSI class includes constants for standard foreground and background colors, as
        /// well as text styles such as bold, underline, and reverse. It also provides dictionaries for mapping color
        /// names to ANSI codes and methods for generating ANSI codes from RGB or hexadecimal color values. These
        /// members can be used to format strings for colored and styled output in terminals that support ANSI escape
        /// sequences.
        /// 
        /// <br></br><br></br>This PowerShell module uses a very sophisticated and advanced technique to determine
        /// whether the host supports ANSI escape sequences or not, described in detail below: <br></br><br></br>
        /// 
        /// I'm lying. It just assumes support for ANSI escape sequences, because they were originally introduced
        /// in the <i>19 goddamned 70s</i>, and if your terminal doesn't support 50-year old technology, well, I just
        /// don't know what to say, aside from good luck and Godspeed, soldier. o7 </remarks>
        public static class ANSI
        {
            public const string BlackFG = "\u001b[30m";
            public const string BlackBG = "\u001b[40m";

            public const string DarkRedFG = "\u001b[31m";
            public const string DarkRedBG = "\u001b[41m";

            public const string DarkGreenFG = "\u001b[32m";
            public const string DarkGreenBG = "\u001b[42m";

            public const string DarkYellowFG = "\u001b[33m";
            public const string DarkYellowBG = "\u001b[43m";

            public const string DarkBlueFG = "\u001b[34m";
            public const string DarkBlueBG = "\u001b[44m";

            public const string DarkMagentaFG = "\u001b[35m";
            public const string DarkMagentaBG = "\u001b[45m";

            public const string DarkCyanFG = "\u001b[36m";
            public const string DarkCyanBG = "\u001b[46m";

            public const string DarkGrayFG = "\u001b[37m";
            public const string DarkGrayBG = "\u001b[47m";

            public const string GrayFG = "\u001b[90m";
            public const string GrayBG = "\u001b[100m";

            public const string RedFG = "\u001b[91m";
            public const string RedBG = "\u001b[101m";

            public const string GreenFG = "\u001b[92m";
            public const string GreenBG = "\u001b[102m";

            public const string YellowFG = "\u001b[93m";
            public const string YellowBG = "\u001b[103m";

            public const string BlueFG = "\u001b[94m";
            public const string BlueBG = "\u001b[104m";

            public const string MagentaFG = "\u001b[95m";
            public const string MagentaBG = "\u001b[105m";

            public const string CyanFG = "\u001b[96m";
            public const string CyanBG = "\u001b[106m";

            public const string WhiteFG = "\u001b[97m";
            public const string WhiteBG = "\u001b[107m";

            public const string Reset = "\u001b[0m";

            public const string Bold = "\u001b[1m";
            public const string Underline = "\u001b[4m";
            public const string Reverse = "\u001b[7m";
            public const string NoUnderline = "\u001b[24m";

            /// <summary>
            /// Provides a mapping of color and style names to their corresponding ANSI escape codes for foreground text
            /// formatting.
            /// </summary>
            /// <remarks>The dictionary includes common color names, style aliases, and special
            /// formatting options such as bold, underline, and reset. The values are ANSI escape code strings that can
            /// be used to format console output. Some entries use alternative names or abbreviations (e.g., "g" for
            /// green, "y" for yellow) for convenience.</remarks>
            public static Dictionary<string, string> FGcolorMap = new Dictionary<string, string>
            {
                { "black", BlackFG },
                { "darkred", DarkRedFG },
                { "darkgreen", DarkGreenFG },
                { "darkyellow", DarkYellowFG },
                { "darkblue", DarkBlueFG },
                { "darkmagenta", DarkMagentaFG },
                { "darkcyan", DarkCyanFG },
                { "darkgray", DarkGrayFG },
                { "gray", GrayFG },
                { "red", RedFG },
                { "sigred", FGCodeHex("CD2E3A") },
                { "ghred", FGCode(255, 123, 114) },
                { "green", GreenFG },
                { "g", GreenFG },
                { "yellow", YellowFG },
                { "y", YellowFG },
                { "blue", BlueFG },
                { "magenta", MagentaFG },
                { "cyan", CyanFG },
                { "white", WhiteFG },
                { "ghwhite", FGCode(240, 246, 252) },
                { "darkgoldenrod", FGCode(175, 135, 0) },
                { "reset", Reset   },
                { "bold", Bold  },
                { "underline", Underline },
                { "nounderline", NoUnderline },
                { "reverse", Reverse }
            };

            /// <summary>
            /// Provides a mapping of color and style names to their corresponding background color or text style escape
            /// codes.
            /// </summary>
            /// <remarks>The dictionary includes common color names, style aliases, and special
            /// formatting options such as bold and underline. The values are typically ANSI escape codes or similar
            /// representations used for console or terminal output formatting. Use the provided keys to retrieve the
            /// appropriate escape code for setting background colors or applying text styles in supported
            /// environments.</remarks>
            public static Dictionary<string, string> BGcolorMap = new Dictionary<string, string>
            {
                { "black", BlackBG },
                { "darkred", DarkRedBG },
                { "darkgreen", DarkGreenBG },
                { "darkyellow", DarkYellowBG },
                { "darkblue", DarkBlueBG },
                { "darkmagenta", DarkMagentaBG },
                { "darkcyan", DarkCyanBG },
                { "darkgray", DarkGrayBG },
                { "gray", GrayBG },
                { "red", RedBG },
                { "sigred", FGCodeHex("CD2E3A") },
                { "ghred", FGCode(255, 123, 114) },
                { "green", GreenBG },
                { "g", GreenBG },
                { "yellow", YellowBG },
                { "y", YellowBG },
                { "blue", BlueBG },
                { "magenta", MagentaBG },
                { "cyan", CyanBG },
                { "white", WhiteBG },
                { "ghwhite", FGCode(240, 246, 252) },
                { "darkgoldenrod", FGCode(175, 135, 0) },
                { "reset", Reset   },
                { "bold", Bold  },
                { "underline", Underline },
                { "nounderline", NoUnderline },
                { "reverse", Reverse }
            };

            /// <summary>
            /// Generates an ANSI escape code string that sets the foreground color using the specified RGB values.
            /// </summary>
            /// <param name="r">The red component of the color. Must be in the range 0 to 255.</param>
            /// <param name="g">The green component of the color. Must be in the range 0 to 255.</param>
            /// <param name="b">The blue component of the color. Must be in the range 0 to 255.</param>
            /// <returns>A string containing the ANSI escape code for the specified foreground color.</returns>
            public static string FGColor(int r, int g, int b)
            {
                return FGCode(r, g, b);
            }

            /// <summary>
            /// Returns the ANSI escape code for the specified foreground color name.
            /// </summary>
            /// <param name="color">The name of the color for which to retrieve the ANSI escape code. The comparison is case-insensitive.</param>
            /// <returns>A string containing the ANSI escape code corresponding to the specified color name.</returns>
            /// <exception cref="ArgumentException">Thrown if the specified color name is not found in the color map.</exception>
            public static string FGColor(string color)
            {
                if (FGcolorMap.ContainsKey(color.ToLower()))
                {
                    return FGcolorMap[color.ToLower()];
                }
                else
                {
                    throw new ArgumentException($"Color '{color}' not found in FGcolorMap.");
                }
            }

            /// <summary>
            /// Returns the specified message string formatted with the given foreground color code.
            /// </summary>
            /// <param name="color">The name of the foreground color to apply. The value is case-insensitive and must correspond
            /// to a key in the color map.</param>
            /// <param name="message">The message to format with the specified foreground color.</param>
            /// <returns>A string containing the message formatted with the specified foreground color code with an ANSI reset code
            /// appended to the end.</returns>
            /// <exception cref="ArgumentException">Thrown if the specified color is not found in the foreground color map.</exception>
            public static string FGColor(string color, string message)
            {
                if (FGcolorMap.ContainsKey(color.ToLower()))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(FGcolorMap[color.ToLower()]);
                    sb.Append(message);
                    sb.Append(Reset);
                    return sb.ToString();
                }
                else
                {
                    throw new ArgumentException($"Color '{color}' not found in FGcolorMap.");
                }
            }

            /// <summary>
            /// Returns the specified message string with the background color set to the given color name using ANSI
            /// escape codes.
            /// </summary>
            /// <param name="color">The name of the background color to apply. The value is case-insensitive and must correspond to a
            /// supported color in the color map.</param>
            /// <param name="message">The message to which the background color will be applied.</param>
            /// <returns>A string containing the ANSI escape code for the specified background color, followed by the message,
            /// and ending with the reset code.</returns>
            /// <exception cref="ArgumentException">Thrown if the specified color is not found in the background color map.</exception>
            public static string BGColor(string color, string message)
            {
                if (BGcolorMap.ContainsKey(color.ToLower()))
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append(BGcolorMap[color.ToLower()]);
                    sb.Append(message);
                    sb.Append(Reset);
                    return sb.ToString();
                }
                else
                {
                    throw new ArgumentException($"Color '{color}' not found in FGcolorMap.");
                }
            }

            /// <summary>
            /// Generates an ANSI escape code string to set the foreground color using the specified RGB values.
            /// </summary>
            /// <remarks>The returned string can be used in terminal output to change the text color
            /// to the specified RGB value. Not all terminals support 24-bit color escape codes; behavior may vary
            /// depending on the environment.</remarks>
            /// <param name="r">The red component of the color, in the range 0 to 255.</param>
            /// <param name="g">The green component of the color, in the range 0 to 255.</param>
            /// <param name="b">The blue component of the color, in the range 0 to 255.</param>
            /// <returns>A string containing the ANSI escape code for the specified foreground color.</returns>
            public static string FGCode(int r, int g, int b)
            {
                return $"\u001b[38;2;{r};{g};{b}m";
            }

            /// <summary>
            /// Formats the specified message string with an ANSI escape sequence to set the foreground color using the
            /// provided RGB values.
            /// </summary>
            /// <remarks>The returned string can be written to a terminal that supports ANSI escape
            /// codes to display the message in the specified color. If the terminal does not support ANSI codes, the
            /// escape sequences may be displayed as plain text.</remarks>
            /// <param name="r">The red component of the foreground color. Must be in the range 0 to 255.</param>
            /// <param name="g">The green component of the foreground color. Must be in the range 0 to 255.</param>
            /// <param name="b">The blue component of the foreground color. Must be in the range 0 to 255.</param>
            /// <param name="message">The message to be formatted with the specified foreground color. Cannot be null.</param>
            /// <returns>A string containing the ANSI escape sequence for the specified RGB color, followed by the message and a
            /// reset sequence.</returns>
            public static string FGCode(int r, int g, int b, string message)
            {
                string finalString = string.Empty;

                StringBuilder sb = new StringBuilder();
                sb.Append($"\u001b[38;2;{r};{g};{b}m");
                sb.Append(message);
                sb.Append(Reset);

                return sb.ToString();
            }

            /// <summary>
            /// Generates an ANSI escape code string to set the foreground color using the specified hex color code.
            /// </summary>
            /// <param name="hexColor">A string representing the color in 6-digit hexadecimal format, with or without a leading '#'.
            /// For example, "#FFFFFF" or "FFFFFF".</param>
            /// <returns>An ANSI escape sequence representing the input hex color.</returns>
            /// <exception cref="ArgumentException">Thrown if hexColor does not represent a valid 6-digit hexadecimal color value.</exception>
            public static string FGCodeHex(string hexColor)
            {
                if (hexColor.StartsWith("#"))
                {
                    hexColor = hexColor[1..];
                }
                if (hexColor.Length != 6)
                {
                    throw new ArgumentException("Hex color must be 6 characters long.");
                }
                int r = Convert.ToInt32(hexColor.Substring(0, 2), 16);
                int g = Convert.ToInt32(hexColor.Substring(2, 2), 16);
                int b = Convert.ToInt32(hexColor.Substring(4, 2), 16);
                return FGCode(r, g, b);
            }

            /// <summary>
            /// Generates a formatted string by applying the specified hexadecimal color to the given message.
            /// </summary>
            /// <param name="hexColor">A string representing the color in 6-digit hexadecimal RGB format (for example, "#FF00AA" or "FF00AA").
            /// The string may optionally start with a '#' character.</param>
            /// <param name="message">The message to which the color formatting will be applied.</param>
            /// <returns>A string containing the message formatted with the specified foreground color.</returns>
            /// <exception cref="ArgumentException">Thrown if hexColor does not represent a valid 6-digit hexadecimal RGB color.</exception>
            public static string FGCodeHex(string hexColor, string message)
            {
                if (hexColor.StartsWith("#"))
                {
                    hexColor = hexColor[1..];
                }
                if (hexColor.Length != 6)
                {
                    throw new ArgumentException("Hex color must be 6 characters long.");
                }

                int r = Convert.ToInt32(hexColor.Substring(0, 2), 16);
                int g = Convert.ToInt32(hexColor.Substring(2, 2), 16);
                int b = Convert.ToInt32(hexColor.Substring(4, 2), 16);

                return FGCode(r, g, b, message);
            }

            /// <summary>
            /// Generates an ANSI escape code string to set the background color using the specified RGB values.
            /// </summary>
            /// <remarks>The returned string can be used in terminal output to change the background
            /// color of subsequent text. Not all terminals support 24-bit (true color) ANSI escape codes; behavior may
            /// vary depending on the environment.</remarks>
            /// <param name="r">The red component of the color, in the range 0 to 255.</param>
            /// <param name="g">The green component of the color, in the range 0 to 255.</param>
            /// <param name="b">The blue component of the color, in the range 0 to 255.</param>
            /// <returns>A string containing the ANSI escape code for the specified background color.</returns>
            public static string BGCode(int r, int g, int b)
            {
                return $"\u001b[48;2;{r};{g};{b}m";
            }

            /// <summary>
            /// Returns the background color code associated with the specified color name.
            /// </summary>
            /// <param name="color">The name of the color to look up. The comparison is case-insensitive.</param>
            /// <returns>A string containing the background color code corresponding to the specified color name.</returns>
            /// <exception cref="ArgumentException">Thrown if the specified color name is not found in the color map.</exception>
            public static string BGColor(string color)
            {
                if (BGcolorMap.ContainsKey(color.ToLower()))
                {
                    return BGcolorMap[color.ToLower()];
                }
                else
                {
                    throw new ArgumentException($"Color '{color}' not found in FGcolorMap.");
                }
            }

            /// <summary>
            /// Returns the specified message string formatted with an ANSI escape sequence to set the background color
            /// using the provided RGB values.
            /// </summary>
            /// <remarks>This method is intended for use in terminals or consoles that support 24-bit
            /// (true color) ANSI escape codes. The output may not display as intended in environments that do not
            /// support these codes.</remarks>
            /// <param name="r">The red component of the background color. Must be in the range 0 to 255.</param>
            /// <param name="g">The green component of the background color. Must be in the range 0 to 255.</param>
            /// <param name="b">The blue component of the background color. Must be in the range 0 to 255.</param>
            /// <param name="message">The message to be displayed with the specified background color. Cannot be null.</param>
            /// <returns>A string containing the ANSI escape sequence for the specified background color, followed by the message
            /// and a reset sequence.</returns>
            public static string BGCode(int r, int g, int b, string message)
            {
                string finalString = string.Empty;
                StringBuilder sb = new StringBuilder();
                sb.Append($"\u001b[48;2;{r};{g};{b}m");
                sb.Append(message);
                sb.Append(Reset);
                return sb.ToString();
            }
        }

        /// <summary>
        /// Initializes static resources or performs setup required by the module before any code in the assembly is
        /// executed.
        /// </summary>
        /// <remarks>This method, along any other ModuleInitializers, are automatically invoked by the runtime before
        /// any other code in the assembly runs. It is not intended to be called directly.</remarks>
        [ModuleInitializer]
        public static void Init()
        {
            _loggingColorRoot ??= new LoggingColorRoot();
        }
    }
}

#pragma warning restore CS8778 // Constant value may overflow at runtime (use 'unchecked' syntax to override)
