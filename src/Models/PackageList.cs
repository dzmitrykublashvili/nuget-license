using System.Collections.Generic;
using NugetUtility.Models;

namespace NugetUtility
{
    public class PackageList : Dictionary<string, Package>
    {
        public PackageList(int capacity = 50) : base(capacity) { }
    }
}