using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mpa.Processor.Protocol;

if (args.Length != 1) throw new ArgumentException("Supply a new public-only golden fixture path.");
using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
var signer = new ProtocolDocumentSigner(key);
const string body = "{\"format\":\"mpa\",\"major\":1,\"minor\":0,\"libraryId\":\"94a81164-e980-4ec1-a566-cec36475b839\",\"kind\":\"user-operation\",\"requiredCapabilities\":[],\"payload\":{\"caption\":\"שלום\",\"sequence\":\"1\"}}";
var signature = signer.Sign(Encoding.UTF8.GetBytes(body));
var fixture = new
{
    note = "Synthetic ephemeral test key. Only public material retained; this is never an authorized product actor.",
    publicKeySpki = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
    body, signature.Algorithm, signature.KeyId, signature.Value
};
using var output = new FileStream(args[0], FileMode.CreateNew, FileAccess.Write, FileShare.None);
JsonSerializer.Serialize(output, fixture, new JsonSerializerOptions { WriteIndented = true });
output.Flush(flushToDisk: true);
Console.WriteLine("Public-only signature fixture generated; ephemeral private key disposed without export.");
