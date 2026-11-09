using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Mpa.Processor.GoogleDrive.Tests")]
// The integration fixture uses the real transport and owned HTTP handler, rather
// than manufacturing observations or opening an external Google connection.
[assembly: InternalsVisibleTo("Mpa.Processor.AppServices.Tests")]
