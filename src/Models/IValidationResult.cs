using System.Collections.Generic;

namespace NugetUtility.Models
{
    public interface IValidationResult<T>
    {
        bool IsValid { get; }
        IReadOnlyCollection<T> InvalidPackages { get; }
    }
}