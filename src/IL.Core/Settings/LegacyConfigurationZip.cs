using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
namespace IL.Core.Settings;

// archive.dart encodes password UTF-16 code units as bytes for native ZIP encryption.
internal static class LegacyConfigurationZip
{
    private static byte[] PasswordBytes(string password)=>password.Select(c=>(byte)c).ToArray();
    public static string ZipCryptoPassword(string password)=>new(password.Select(c=>(char)(byte)c).ToArray());
    public static byte[] Export(string name,byte[] payload,string password)
    {
        using var compressed=new MemoryStream();using(var deflate=new DeflateStream(compressed,CompressionLevel.Optimal,true))deflate.Write(payload);
        var salt=RandomNumberGenerator.GetBytes(16);var keys=Rfc2898DeriveBytes.Pbkdf2(PasswordBytes(password),salt,1000,HashAlgorithmName.SHA1,66);var ciphertext=Ctr(compressed.ToArray(),keys[..32]);var mac=HMACSHA1.HashData(keys[32..64],ciphertext)[..10];var crc=new ICSharpCode.SharpZipLib.Checksum.Crc32();crc.Update(payload);
        var filename=Encoding.UTF8.GetBytes(name);var length=ciphertext.Length+28;using var output=new MemoryStream();using var w=new BinaryWriter(output,Encoding.UTF8,true);
        void Extra(){w.Write((ushort)0x9901);w.Write((ushort)7);w.Write((ushort)1);w.Write((ushort)0x4541);w.Write((byte)3);w.Write((ushort)8);}
        w.Write(0x04034b50u);w.Write((ushort)51);w.Write((ushort)2049);w.Write((ushort)99);w.Write(0u);w.Write((uint)crc.Value);w.Write((uint)length);w.Write((uint)payload.Length);w.Write((ushort)filename.Length);w.Write((ushort)11);w.Write(filename);Extra();w.Write(salt);w.Write(keys[64..66]);w.Write(ciphertext);w.Write(mac);
        var directory=(uint)output.Position;w.Write(0x02014b50u);w.Write((ushort)51);w.Write((ushort)51);w.Write((ushort)2049);w.Write((ushort)99);w.Write(0u);w.Write((uint)crc.Value);w.Write((uint)length);w.Write((uint)payload.Length);w.Write((ushort)filename.Length);w.Write((ushort)11);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)0);w.Write(0u);w.Write(0u);w.Write(filename);Extra();var directorySize=(uint)output.Position-directory;
        w.Write(0x06054b50u);w.Write((ushort)0);w.Write((ushort)0);w.Write((ushort)1);w.Write((ushort)1);w.Write(directorySize);w.Write(directory);w.Write((ushort)0);w.Flush();return output.ToArray();
    }
    public static byte[] ReadAes(byte[] archive,ZipEntry entry,string password)
    {
        using var source=new MemoryStream(archive);using var r=new BinaryReader(source);source.Position=entry.Offset;if(r.ReadUInt32()!=0x04034b50u)throw new InvalidDataException("Invalid ZIP header");source.Position=entry.Offset+26;var nameLength=r.ReadUInt16();var extraLength=r.ReadUInt16();source.Position=entry.Offset+30+nameLength+extraLength;
        var keyBytes=entry.AESKeySize/8;var salt=r.ReadBytes(keyBytes/2);var verify=r.ReadBytes(2);var dataLength=checked((int)entry.CompressedSize-salt.Length-12);if(dataLength<0)throw new InvalidDataException("Invalid encrypted ZIP length");var ciphertext=r.ReadBytes(dataLength);var mac=r.ReadBytes(10);if(ciphertext.Length!=dataLength||mac.Length!=10)throw new InvalidDataException("Truncated encrypted ZIP");var keys=Rfc2898DeriveBytes.Pbkdf2(PasswordBytes(password),salt,1000,HashAlgorithmName.SHA1,keyBytes*2+2);if(!CryptographicOperations.FixedTimeEquals(verify,keys[(keyBytes*2)..])||!CryptographicOperations.FixedTimeEquals(mac,HMACSHA1.HashData(keys[keyBytes..(keyBytes*2)],ciphertext)[..10]))throw new InvalidDataException("Incorrect password or damaged ZIP authentication");var compressed=Ctr(ciphertext,keys[..keyBytes]);
        if(entry.CompressionMethod==CompressionMethod.Stored)return compressed;if(entry.CompressionMethod!=CompressionMethod.Deflated)throw new InvalidDataException("Unsupported ZIP compression");using var data=new MemoryStream(compressed);using var deflate=new DeflateStream(data,CompressionMode.Decompress);using var output=new MemoryStream();deflate.CopyTo(output);return output.ToArray();
    }
    private static byte[] Ctr(byte[] input,byte[] key)
    {
        using var aes=Aes.Create();aes.Key=key;aes.Mode=CipherMode.ECB;aes.Padding=PaddingMode.None;using var transform=aes.CreateEncryptor();var output=new byte[input.Length];var counter=new byte[16];var pad=new byte[16];uint value=1;
        for(var offset=0;offset<input.Length;offset+=16){System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(counter,value++);transform.TransformBlock(counter,0,16,pad,0);for(var i=0;i<16&&offset+i<input.Length;i++)output[offset+i]=(byte)(input[offset+i]^pad[i]);}return output;
    }
}
