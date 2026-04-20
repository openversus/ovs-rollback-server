using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OVS.Rollback.Common
{
    public static class StringExtensions
    {

        extension(string)
        {
            /// <summary>
            /// Combines two path segments into a single path string using the directory separator character.
            /// </summary>
            /// <remarks>This operator provides a convenient syntax for combining paths. It uses the
            /// platform-specific directory separator. If either parameter is an empty string, the result is the other
            /// parameter.</remarks>
            /// <param name="parent">The first path segment, typically representing the parent directory.</param>
            /// <param name="child">The second path segment, typically representing the child directory or file name.</param>
            /// <returns>A string containing the combined path of the parent and child segments.</returns>
            public static string operator /(string? parent, string? child)
            {
                string safeParent = parent ?? string.Empty;
                string safeChild = child ?? string.Empty;
                return Path.Combine(safeParent, safeChild);
            }
        }

        extension(string? inputString)
        {
            /// <summary>
            /// Determines whether the specified string is not null, empty, or consists only of white-space characters.
            /// </summary>
            /// <returns><see langword="true"/> if string is not null, not empty, and contains at least one
            /// non-whitespace character; otherwise, <see langword="false"/>.</returns>
            /// 
            public bool IsNotNullOrWhiteSpace => !string.IsNullOrWhiteSpace(inputString);

            /// <summary>
            /// Determines whether the specified string is not null, empty, or consists only of white-space characters.
            /// </summary>
            /// <returns><see langword="true"/> if string is not null, not empty, and contains at least one
            /// non-whitespace character; otherwise, <see langword="false"/>.</returns>
            public bool NotNullOrWhiteSpace => !string.IsNullOrWhiteSpace(inputString);


            /// <summary>
            /// Determines whether the specified string is not null or an empty string ("").
            /// </summary>
            /// <returns><see langword="true"/> if string is not null, not empty, and contains at least one
            /// non-whitespace character; otherwise, <see langword="false"/>.</returns>
            public bool NotNullOrEmpty => !string.IsNullOrEmpty(inputString);

            /// <summary>
            /// Indicates whether a specified string is null, empty, or consists only of white-space characters.
            /// </summary>
            /// <returns><see langword="true"/> if string is null, empty, or contains only whitespace characters;
            /// otherwise, <see langword="false"/>.</returns>
            public bool StringIsNullOrWhiteSpace => string.IsNullOrWhiteSpace(inputString);

            /// <summary>
            /// Determines whether the specified string is null or an empty string ("").
            /// </summary>
            /// <returns><see langword="true"/> if string is null, empty, or contains only whitespace characters;
            /// otherwise, <see langword="false"/>.</returns>
            public bool StringIsNullOrEmpty => string.IsNullOrEmpty(inputString);

            /// <summary>
            /// Determines whether the specified string is not null or an empty string ("").
            /// </summary>
            /// <returns><see langword="true"/> if string is not null, not empty, and contains at least one
            /// non-whitespace character; otherwise, <see langword="false"/>.</returns>

            public bool IsNotNullOrEmpty => !string.IsNullOrEmpty(inputString);
        }
    }
}
