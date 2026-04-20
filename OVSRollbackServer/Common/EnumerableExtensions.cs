using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace OVS.Rollback.Common
{
    public static class EnumerableExtensions
    {
        extension<T>(IEnumerable<T> source)
        {
            public bool IsEmpty => !source.Any();

            public bool IsNullOrEmpty => source == null || !source.Any();

            public bool IsNull => source == null;
        }
    }
}
