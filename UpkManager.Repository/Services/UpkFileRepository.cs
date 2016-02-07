﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using AutoMapper;

using STR.Common.Extensions;

using UpkManager.Domain.Contracts;
using UpkManager.Domain.Messages.FileHeader;
using UpkManager.Domain.Models;
using UpkManager.Domain.Models.Tables;
using UpkManager.Entities;
using UpkManager.Entities.Compression;
using UpkManager.Entities.Tables;

using UpkManager.Repository.Constants;
using UpkManager.Repository.Contracts;


namespace UpkManager.Repository.Services {

  [Export(typeof(IUpkFileRepository))]
  public class UpkFileRepository : IUpkFileRepository {

    #region Private Fields

    private readonly IMapper mapper;

    private readonly ILzoCompressor lzoCompressor;

    #endregion Private Fields

    #region Constructor

    [ImportingConstructor]
    public UpkFileRepository(IMapper Mapper, ILzoCompressor LzoCompressor) {
      mapper = Mapper;

      lzoCompressor = LzoCompressor;
    }

    #endregion Constructor

    #region IUpkFileRepository Implementation

    public async Task<DomainHeader> LoadAndParseUpk(DomainHeader Header, bool SkipProperties, bool SkipParsing, Action<LoadProgressMessage> LoadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Loading File..." };

      LoadProgress?.Invoke(message);

      byte[] data = await Task.Run(() => File.ReadAllBytes(Header.FullFilename));

      message.Text = "Parsing Header...";

      LoadProgress?.Invoke(message);

      int index = 0;

      UpkHeader upkHeader = new UpkHeader();

      upkHeader.ReadUpkHeader(data, ref index);

      if (upkHeader.CompressedChunks.Count > 0) {
        if ((upkHeader.CompressionFlags & (CompressionFlag.LZO | CompressionFlag.LZO_ENC)) == 0) {
          message.IsComplete = true;

          LoadProgress?.Invoke(message);

          mapper.Map(upkHeader, Header);

          return Header;
        }

        data = await decompressChunksAsync(upkHeader.CompressedChunks, upkHeader.CompressionFlags, LoadProgress);
      }

      readNameTable(data, upkHeader, LoadProgress);

      readImportTable(data, upkHeader, LoadProgress);
      readExportTable(data, upkHeader, LoadProgress);

      readDependsTable(data, upkHeader);

      patchPointers(upkHeader);

      await readExportTableObjects(data, upkHeader, SkipProperties, SkipParsing, LoadProgress);

      mapper.Map(upkHeader, Header);

      message.IsComplete = true;

      LoadProgress?.Invoke(message);

      return Header;
    }

    public async Task SaveObject(DomainExportTableEntry Export, string Filename) {
      ExportTableEntry export = mapper.Map<ExportTableEntry>(Export);

      await Task.Run(() => export.UpkObject.SaveObject(Filename));
    }

    public Stream GetObjectStream(DomainExportTableEntry Export) {
      ExportTableEntry export = mapper.Map<ExportTableEntry>(Export);

      return export.UpkObject.GetObjectStream();
    }

    #endregion IUpkFileRepository Implementation

    #region Private Methods

    private static void readNameTable(byte[] data, UpkHeader header, Action<LoadProgressMessage> loadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Parsing Name Table", Current = 0, Total = header.NameTableCount };

      loadProgress?.Invoke(message);

      int index = header.NameTableOffset;

      for(int i = 0; i < header.NameTableCount; ++i) {
        NameTableEntry name = new NameTableEntry { Index = i };

        name.ReadNameTableEntry(data, ref index);

        header.NameTable.Add(name);

        if (header.NameTableCount > 1000) {
          message.Current += 1;

          loadProgress?.Invoke(message);
        }
      }
    }

    private static void readImportTable(byte[] data, UpkHeader header, Action<LoadProgressMessage> loadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Parsing Import Table", Current = 0, Total = header.ImportTableCount };

      loadProgress?.Invoke(message);

      int index = header.ImportTableOffset;

      for(int i = 0; i < header.ImportTableCount; ++i) {
        ImportTableEntry import = new ImportTableEntry { TableIndex = -(i + 1) };

        import.ReadImportTableEntry(data, ref index, header.NameTable);

        header.ImportTable.Add(import);

        if (header.ImportTableCount > 1000) {
          message.Current += 1;

          loadProgress?.Invoke(message);
        }
      }
    }

    private static void readExportTable(byte[] data, UpkHeader header, Action<LoadProgressMessage> loadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Parsing Export Table", Current = 0, Total = header.ExportTableCount };

      loadProgress?.Invoke(message);

      int index = header.ExportTableOffset;

      for(int i = 0; i < header.ExportTableCount; ++i) {
        ExportTableEntry export = new ExportTableEntry { TableIndex = i + 1 };

        export.ReadExportTableEntry(data, ref index, header.NameTable);

        header.ExportTable.Add(export);

        if (header.ExportTableCount > 1000) {
          message.Current += 1;

          loadProgress?.Invoke(message);
        }
      }
    }

    private static void readDependsTable(byte[] data, UpkHeader header) {
      header.DependsTable = new byte[header.Size - header.DependsTableOffset];

      Array.ConstrainedCopy(data, header.DependsTableOffset, header.DependsTable, 0, header.Size - header.DependsTableOffset);
    }

    private static async Task readExportTableObjects(byte[] data, UpkHeader header, bool skipProperties, bool skipParse, Action<LoadProgressMessage> loadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Parsing Export Table Objects", Current = 0, Total = header.ExportTableCount };

      loadProgress?.Invoke(message);

      await header.ExportTable.ForEachAsync(export => {
        if (header.ExportTableCount > 1000) {
          message.Current += 1;

          loadProgress?.Invoke(message);
        }

        return Task.Run(() => export.ReadObjectType(data, header, skipProperties, skipParse));
      });
    }

    private async Task<byte[]> decompressChunksAsync(List<CompressedChunk> chunks, uint flags, Action<LoadProgressMessage> loadProgress) {
      LoadProgressMessage message = new LoadProgressMessage { Text = "Decompressing", Current = 0, Total = chunks.SelectMany(chunk => chunk.Header.Blocks).Count() };

      int totalSize = chunks.Min(ch => ch.UncompressedOffset);

      totalSize = chunks.SelectMany(ch => ch.Header.Blocks).Aggregate(totalSize, (total, block) => total + block.UncompressedSize);

      byte[] data = new byte[totalSize];

      foreach(CompressedChunk chunk in chunks) {
        byte[] chunkData = new byte[chunk.Header.Blocks.Sum(block => block.UncompressedSize)];

        int uncompressedOffset = 0;

        foreach(CompressedChunkBlock block in chunk.Header.Blocks) {
          if ((flags & CompressionFlag.LZO_ENC) == CompressionFlag.LZO_ENC) decryptChunk(block.CompressedData);

          byte[] decompressed = await lzoCompressor.DecompressAsync(block.CompressedData, block.UncompressedSize);

//        block.UncompressedOffset = chunk.UncompressedOffset + uncompressedOffset;

          int offset = uncompressedOffset;

          await Task.Run(() => Array.ConstrainedCopy(decompressed, 0, chunkData, offset, block.UncompressedSize));

          uncompressedOffset += block.UncompressedSize;

          message.Current += 1;

          loadProgress?.Invoke(message);
        }

        await Task.Run(() => Array.ConstrainedCopy(chunkData, 0, data, chunk.UncompressedOffset, chunk.Header.UncompressedSize));
      }

      return data;
    }
    //
    // Following four methods are from:
    //
    // https://github.com/gildor2/UModel/blob/c871f9d534e0bd42a17b4d4268c0ecc59dd7191e/Unreal/UnPackage.cpp
    //
    private static void patchPointers(UpkHeader header) {
      uint code1 = ((uint)header.Size             & 0xff) << 24
                 | ((uint)header.NameTableCount   & 0xff) << 16
                 | ((uint)header.NameTableOffset  & 0xff) << 8
                 | ((uint)header.ExportTableCount & 0xff);

      int code2 = (header.ExportTableOffset + header.ImportTableCount + header.ImportTableOffset) & 0x1f;

      List<ExportTableEntry> exports = header.ExportTable;

      for(int i = 0; i < exports.Count; ++i) {
        uint size   = (uint)exports[i].SerialDataSize;
        uint offset = (uint)exports[i].SerialDataOffset;

        decodePointer(ref size,   code1, code2, i);
        decodePointer(ref offset, code1, code2, i);

        exports[i].SerialDataSize   = (int)size;
        exports[i].SerialDataOffset = (int)offset;
      }
    }

    private static void decodePointer(ref uint value, uint code1, int code2, int index) {
      uint tmp1 = ror32(value, (index + code2) & 0x1f);
      uint tmp2 = ror32(code1, index % 32);

      value = tmp2 ^ tmp1;
    }

    private static uint ror32(uint val, int shift) {
      return (val >> shift) | (val << (32 - shift));
    }

    private static void decryptChunk(byte[] data) {
      if (data.Length < 32) return;

//    const string key = "qiffjdlerdoqymvketdcl0er2subioxq";

      byte[] key = { 0x71, 0x69, 0x66, 0x66, 0x6a, 0x64, 0x6c, 0x65, 0x72, 0x64, 0x6f, 0x71, 0x79, 0x6d, 0x76, 0x6b, 0x65, 0x74, 0x64, 0x63, 0x6c, 0x30, 0x65, 0x72, 0x32, 0x73, 0x75, 0x62, 0x69, 0x6f, 0x78, 0x71 };

      for(int i = 0; i < data.Length; ++i) data[i] ^= key[i % 32];
    }

    #endregion Private Methods

  }

}
