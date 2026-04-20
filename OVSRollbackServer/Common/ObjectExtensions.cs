using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;

namespace OVS.Rollback.Common
{
    public static class ObjectExtensions
    {
        extension (object? obj)
        {
            public bool IsNull => null == obj;
        }
    }
}
