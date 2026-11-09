using System.Buffers.Binary;
using System.Text;

namespace Mpa.Processor.Core;

/// <summary>Bounds the untrusted central directory BEFORE ZipArchive builds its
/// entry objects. Single-disk ZIP/ZIP64 only; no archive extraction or repair.</summary>
internal static class TakeoutZipDirectoryBounds
{
    internal const int MaximumEntries=100000;
    internal const long MaximumDirectoryBytes=32*1024*1024;
    internal const long MaximumArchiveBytes=1024L*1024*1024*1024;
    private static readonly UTF8Encoding StrictUtf8=new(false,true);
    internal static int Verify(Stream input,CancellationToken token)
    {
        if(!input.CanRead||!input.CanSeek||input.Length is <22 or >MaximumArchiveBytes)throw new InvalidDataException("Takeout ZIP needs a bounded seekable local snapshot.");
        token.ThrowIfCancellationRequested();var length=input.Length;var tail=new byte[(int)Math.Min(length,65535+22)];
        input.Position=length-tail.Length;input.ReadExactly(tail);var end=-1;
        for(var at=tail.Length-22;at>=0;at--)
            if(U32(tail,at)==0x06054b50&&at+22+U16(tail,at+20)==tail.Length){end=at;break;}
        if(end<0)throw new InvalidDataException("Takeout ZIP has no complete end record.");
        var endOffset=length-tail.Length+end;
        if(U16(tail,end+4)!=0||U16(tail,end+6)!=0)throw new InvalidDataException("Spanned ZIP disks are unsupported; select complete Takeout archive parts.");
        ulong count=U16(tail,end+10),bytes=U32(tail,end+12),offset=U32(tail,end+16);var centralEnd=endOffset;
        var hasLocator=false;
        if(endOffset>=20)
        {var signature=new byte[4];input.Position=endOffset-20;input.ReadExactly(signature);hasLocator=U32(signature,0)==0x07064b50;}
        if(count==ushort.MaxValue||bytes==uint.MaxValue||offset==uint.MaxValue||hasLocator)
        {
            if(endOffset<76)throw new InvalidDataException("Takeout ZIP64 locator/end record is missing.");
            var locator=new byte[20];input.Position=endOffset-20;input.ReadExactly(locator);
            if(U32(locator,0)!=0x07064b50||U32(locator,4)!=0||U32(locator,16)!=1)throw new InvalidDataException("Takeout ZIP64 must reside on one complete disk.");
            var location=U64(locator,8);
            if(location>(ulong)(endOffset-20-56))throw new InvalidDataException("Takeout ZIP64 end record is outside its snapshot.");
            var record=new byte[56];input.Position=(long)location;input.ReadExactly(record);var recordBytes=U64(record,4);
            if(U32(record,0)!=0x06064b50||recordBytes is <44 or >65536||location+12+recordBytes!=(ulong)(endOffset-20)||U32(record,16)!=0||U32(record,20)!=0||U64(record,24)!=U64(record,32))
                throw new InvalidDataException("Takeout ZIP64 end record is inconsistent or unsupported.");
            count=U64(record,32);bytes=U64(record,40);offset=U64(record,48);centralEnd=(long)location;
            if(U16(tail,end+8)!=ushort.MaxValue&&U16(tail,end+8)!=count||U16(tail,end+10)!=ushort.MaxValue&&U16(tail,end+10)!=count||U32(tail,end+12)!=uint.MaxValue&&U32(tail,end+12)!=bytes||U32(tail,end+16)!=uint.MaxValue&&U32(tail,end+16)!=offset)
                throw new InvalidDataException("Takeout ZIP and ZIP64 end records disagree.");
        }
        else if(U16(tail,end+8)!=count)throw new InvalidDataException("Takeout ZIP entry counts disagree.");
        if(count>MaximumEntries||bytes>(ulong)MaximumDirectoryBytes||offset>(ulong)centralEnd||bytes!=(ulong)centralEnd-offset||count*46>bytes)
            throw new InvalidDataException("Takeout ZIP central directory exceeds its bounded entry/byte budget or physical range.");
        input.Position=(long)offset;var header=new byte[46];
        for(ulong index=0;index<count;index++)
        {
            token.ThrowIfCancellationRequested();if(input.Position>centralEnd-46)throw new InvalidDataException("Takeout ZIP central directory is truncated.");
            input.ReadExactly(header);var name=U16(header,28);var extra=U16(header,30);var comment=U16(header,32);
            if(U32(header,0)!=0x02014b50||name is <1 or >4096||U16(header,34)!=0||(U16(header,8)&0x2041)!=0||U16(header,10) is not(0 or 8 or 9)||input.Position+name+extra+comment>centralEnd)
                throw new InvalidDataException("Takeout ZIP contains an encrypted, spanned, malformed or unsupported entry.");
            var nameBytes=new byte[name];input.ReadExactly(nameBytes);
            try {_=StrictUtf8.GetCharCount(nameBytes);}
            catch(DecoderFallbackException error){throw new InvalidDataException("Takeout ZIP filename encoding is unsupported or malformed.",error);}
            input.Position+=extra+comment;
        }
        if(input.Position!=centralEnd)throw new InvalidDataException("Takeout ZIP has unexplained central-directory bytes.");
        input.Position=0;return(int)count;
    }
    private static ushort U16(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(at,2));
    private static uint U32(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at,4));
    private static ulong U64(byte[] bytes,int at)=>BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at,8));
}
