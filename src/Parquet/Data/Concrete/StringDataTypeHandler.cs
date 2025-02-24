﻿using System;
using System.Buffers;
using System.IO;
using System.Text;

namespace Parquet.Data.Concrete
{
   class StringDataTypeHandler : BasicDataTypeHandler<string>
   {
      private static readonly UTF8Encoding E = new UTF8Encoding();
      private static readonly ArrayPool<byte> _bytePool = ArrayPool<byte>.Shared;
      private static readonly ArrayPool<string> _stringPool = ArrayPool<string>.Shared;

      private static byte[] _encodingBuf;
      
      public StringDataTypeHandler() : base(DataType.String, Thrift.Type.BYTE_ARRAY, Thrift.ConvertedType.UTF8)
      {
      }

      public override Array GetArray(int minCount, bool rent, bool isNullable)
      {
         if (rent)
         {
            return _stringPool.Rent(minCount);
         }

         return new string[minCount];
      }

      public override bool IsMatch(Thrift.SchemaElement tse, ParquetOptions formatOptions)
      {
         return tse.__isset.type &&
            tse.Type == Thrift.Type.BYTE_ARRAY &&
            (
               (tse.__isset.converted_type && tse.Converted_type == Thrift.ConvertedType.UTF8) ||
               formatOptions.TreatByteArrayAsString
            );
      }

      public override int Read(BinaryReader reader, Thrift.SchemaElement tse, Array dest, int offset)
      {
         int remLength = (int)(reader.BaseStream.Length - reader.BaseStream.Position);

         if (remLength == 0)
            return 0;

         string[] tdest = (string[])dest;

         //reading string one by one is extremely slow, read all data

         byte[] allBytes = _bytePool.Rent(remLength);
         reader.BaseStream.Read(allBytes, 0, remLength);
         int destIdx = offset;
         try
         {
            Span<byte> span = allBytes.AsSpan(0, remLength);   //will be passed as input in future versions

            int spanIdx = 0;

            while (spanIdx < span.Length && destIdx < tdest.Length)
            {
               int length = span.Slice(spanIdx, 4).ReadInt32();
               string s = E.GetString(allBytes, spanIdx + 4, length);
               tdest[destIdx++] = s;
               spanIdx = spanIdx + 4 + length;
            }
         }
         finally
         {
            _bytePool.Return(allBytes);
         }

         return destIdx - offset;
      }

      private static string ReadSingle(BinaryReader reader, Thrift.SchemaElement tse, int length, bool hasLengthPrefix)
      {
         if (length == -1)
         {
            if (hasLengthPrefix)
            {
               length = reader.ReadInt32();
            }
            else
            {
               length = (int)reader.BaseStream.Length;
            }
         }

         byte[] data = reader.ReadBytes(length);
         return Encoding.UTF8.GetString(data);
      }

      protected override string ReadSingle(BinaryReader reader, Thrift.SchemaElement tse, int length)
      {
         return ReadSingle(reader, tse, length, true);
      }

      public override ArrayView PackDefinitions(Array data, int maxDefinitionLevel, out int[] definitions, out int definitionsLength, out int nullCount)
      {
         return PackDefinitions((string[])data, maxDefinitionLevel, out definitions, out definitionsLength, out nullCount);
      }

      public override Array UnpackDefinitions(Array src, int[] definitionLevels, int maxDefinitionLevel, out bool[] hasValueFlags)
      {
         return UnpackGenericDefinitions((string[])src, definitionLevels, maxDefinitionLevel, out hasValueFlags);
      }

      private static void WriteOne(BinaryWriter writer, string value, bool includeLengthPrefix)
      {
         if (value == null || value.Length == 0)
         {
            if (includeLengthPrefix)
            {
               writer.Write((int)0);
            }
         }
         else
         {
            //transofrm to byte array first, as we need the length of the byte buffer, not string length

            int needLength = value.Length * 3;
            if(_encodingBuf == null || _encodingBuf.Length < needLength)
            {
               if(_encodingBuf != null)
               {
                  _bytePool.Return(_encodingBuf);
               }

               _encodingBuf = _bytePool.Rent(needLength);
            }

            // this can write directly to buffer after I kill binarystream
            int bytesWritten = E.GetBytes(value, 0, value.Length, _encodingBuf, 0);

            if (includeLengthPrefix)
            {
               writer.Write(bytesWritten);
            }
            writer.Write(_encodingBuf, 0, bytesWritten);
         }
      }

      protected override void WriteOne(BinaryWriter writer, string value)
      {
         WriteOne(writer, value, true);
      }

      public override int Compare(string x, string y)
      {
         return string.CompareOrdinal(x, y);
      }

      public override bool Equals(string x, string y)
      {
         return string.CompareOrdinal(x, y) == 0;
      }

      public override byte[] PlainEncode(Thrift.SchemaElement tse, string x)
      {
         using (var ms = new MemoryStream())
         {
            using (var bs = new BinaryWriter(ms))
            {
               WriteOne(bs, x, false);
            }

            return ms.ToArray();
         }
      }

      public override object PlainDecode(Thrift.SchemaElement tse, byte[] encoded)
      {
         if (encoded == null) return null;

         using (var ms = new MemoryStream(encoded))
         {
            using (var br = new BinaryReader(ms))
            {
               string element = ReadSingle(br, null, -1, false);
               return element;
            }
         }
      }
   }
}