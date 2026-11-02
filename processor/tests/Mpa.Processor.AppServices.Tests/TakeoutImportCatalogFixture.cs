using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mpa.Processor.Core;

namespace Mpa.Processor.AppServices.Tests;

public sealed partial class AccountServiceTests
{
    // Add the actual facts-chunk endpoint to the existing JSON-only initial-policy
    // fixture. Preserve all its original assertions for non-gzip writes.
    private static void InstallImportedCatalogFixture(CurrentReaderFixture fixture)
    {
        var create=fixture.Data.Setup.Accounts.Readers.WorkspaceCreate!;
        fixture.Data.Setup.Accounts.Readers.WorkspaceCreate=async(folder,file,mime,payload,hash,token)=>
        {
            if(mime!="application/gzip")return await create(folder,file,mime,payload,hash,token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var captured=await fixture.Data.Setup.Accounts.State.ExecuteAsync(database=>
            {
                var repository=new WorkspacePublicationRepository(database,fixture.Data.Budget);
                return repository.Identities().Select(id=>repository.Read(id)??throw new InvalidDataException("The captured metadata publication disappeared."))
                    .Single(value=>value.FileId==file);
            },token).ConfigureAwait(false);
            Assert.AreEqual("facts-chunk",captured.Role);Assert.AreEqual(folder,captured.Scope.FolderId);Assert.AreEqual(mime,captured.MimeType);
            Assert.AreEqual(hash,captured.Sha256);Assert.AreEqual(hash,Convert.ToHexStringLower(SHA256.HashData(payload.Span)));
            CollectionAssert.AreEqual(captured.Payload.ToArray(),payload.ToArray());fixture.Data.Creates++;
            if(!fixture.Data.Cloud.TryGetValue(file,out var stored))
            {stored=new(captured.Scope.AccountId,file,folder,7,payload.ToArray()){MimeType=mime};fixture.Data.Cloud.Add(file,stored);}
            CollectionAssert.AreEqual(payload.ToArray(),stored.Bytes.ToArray());fixture.Data.MetadataMimeTypes[file]=mime;return stored;
        };
    }
}
