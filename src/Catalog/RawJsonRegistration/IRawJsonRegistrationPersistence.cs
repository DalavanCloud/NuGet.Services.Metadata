using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Services.Metadata.Catalog.Registration;

namespace NuGet.Services.Metadata.Catalog.RawJsonRegistration
{
    public interface IRawJsonRegistrationPersistence
    {
        Task<IDictionary<RegistrationEntryKey, RawJsonRegistrationCatalogEntry>> Load(CancellationToken cancellationToken);
        Task Save(IDictionary<RegistrationEntryKey, RawJsonRegistrationCatalogEntry> registration, CancellationToken cancellationToken);
    }
}