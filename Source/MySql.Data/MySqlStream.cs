// Copyright � 2004, 2016, Oracle and/or its affiliates. All rights reserved.
//
// MySQL Connector/NET is licensed under the terms of the GPLv2
// <http://www.gnu.org/licenses/old-licenses/gpl-2.0.html>, like most 
// MySQL Connectors. There are special exceptions to the terms and 
// conditions of the GPLv2 as it is applied to this software, see the 
// FLOSS License Exception
// <http://www.mysql.com/about/legal/licensing/foss-exception.html>.
//
// This program is free software; you can redistribute it and/or modify 
// it under the terms of the GNU General Public License as published 
// by the Free Software Foundation; version 2 of the License.
//
// This program is distributed in the hope that it will be useful, but 
// WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY 
// or FITNESS FOR A PARTICULAR PURPOSE. See the GNU General Public License 
// for more details.
//
// You should have received a copy of the GNU General Public License along 
// with this program; if not, write to the Free Software Foundation, Inc., 
// 51 Franklin St, Fifth Floor, Boston, MA 02110-1301  USA

using System;
using System.IO;
using System.Text;
using MySql.Data.MySqlClient;
using System.IO.Compression;

namespace MySql.Data.MySqlClient
{
  /// <summary>
  /// Summary description for MySqlStream.
  /// </summary>
  internal class MySqlStream
  {
    // header used for compressed and non-compressed packets
    private readonly byte[] header = new byte[7];
    private readonly MySqlPacket _packet;
    private readonly Stream baseStream;
    private MemoryStream compBuffer = new MemoryStream(1024);
    private Stream stream;
    private int bytesLeft = 0;
    private bool compressed = false;

    public MySqlStream(Encoding encoding)
    {
      // we have no idea what the real value is so we start off with the max value
      // The real value will be set in NativeDriver.Configure()
      MaxPacketSize = ulong.MaxValue;

      // we default maxBlockSize to MaxValue since we will get the 'real' value in 
      // the authentication handshake and we know that value will not exceed 
      // true maxBlockSize prior to that.

      MaxBlockSize = Int32.MaxValue;

      _packet = new MySqlPacket(encoding);
    }

    public MySqlStream(Stream baseStream, Encoding encoding, bool compress)
      : this(encoding)
    {
      this.compressed = compress;
      this.baseStream = baseStream;
      stream = this.baseStream;
    }

    public void Close()
    {
      stream.Dispose();
    }

#region Properties

    public Encoding Encoding
    {
      get { return _packet.Encoding; }
      set { _packet.Encoding = value; }
    }

    public void ResetTimeout(int timeout)
    {
      //TODO: fix this
     // _timedStream.ResetTimeout(timeout);
    }

    public byte SequenceByte { get; set; }

    public int MaxBlockSize { get; set; }

    public ulong MaxPacketSize { get; set; }

#endregion

#region Packet methods

    /// <summary>
    /// ReadPacket is called by NativeDriver to start reading the next
    /// packet on the stream.
    /// </summary>
    public MySqlPacket ReadPacket()
    {
      //Debug.Assert(packet.Position == packet.Length);

      // make sure we have read all the data from the previous packet
      //Debug.Assert(HasMoreData == false, "HasMoreData is true in OpenPacket");

      LoadPacket();
      return _packet;
    }

    /// <summary>
    /// Reads the specified number of bytes from the stream and stores them at given 
    /// offset in the buffer.
    /// Throws EndOfStreamException if not all bytes can be read.
    /// </summary>
    /// <param name="stream">Stream to read from</param>
    /// <param name="buffer"> Array to store bytes read from the stream </param>
    /// <param name="offset">The offset in buffer at which to begin storing the data read from the current stream. </param>
    /// <param name="count">Number of bytes to read</param>
    internal static void ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
      int numRead = 0;
      int numToRead = count;
      while (numToRead > 0)
      {
        int read = stream.Read(buffer, offset + numRead, numToRead);
        if (read == 0)
        {
          throw new EndOfStreamException();
        }
        numRead += read;
        numToRead -= read;
      }
    }

    /// <summary>
    /// LoadPacket loads up and decodes the header of the incoming packet.
    /// </summary>
    public void LoadPacket()
    {
      try
      {
        ReadCompressedIfNecessary();

        _packet.Clear();
        _packet.Length = 0;

        while (true)
        {
          ReadFully(stream, header, 0, 4);
          SequenceByte = (byte)(header[3] + 1);
          int length = (int)(header[0] + (header[1] << 8) + (header[2] << 16));

          // make roo for the next block
          _packet.Length += length;
          byte[] buffer = _packet.Buffer;
          ReadFully(stream, buffer, _packet.Position, length);
          _packet.Position += length;

          // if this block was < maxBlock then it's last one in a multipacket series
          if (length < MaxBlockSize) break;
        }
#if DUMP_PACKETS
        _packet.Dump(true);
#endif
        _packet.Position = 0;
      }
      catch (IOException ioex)
      {
        throw new MySqlException(Resources.ReadFromStreamFailed, true, ioex);
      }
    }

    // Read in and decompress the packet if necessary
    private void ReadCompressedIfNecessary()
    {
      if (!compressed || bytesLeft > 0) return;

      if (stream is DeflateStream && stream != null)
        stream.Dispose();

      // see if we still have packets to read
      //if (stream.Position < stream.Length) return;

      // we don't so we load up more
      ReadFully(baseStream, header, 0, 7);
      int comp_len = header[0] + (header[1]<<8) + (header[2]<<16);
      int uncomp_len = header[4] + (header[5] << 8) + (header[6] << 16);

      if (uncomp_len == 0)
      {
        bytesLeft = comp_len;
        stream = baseStream;
//        uncomp_len = comp_len;
  //      compStream.Capacity = uncomp_len;
    //    ReadFully(stream, compStream.GetBuffer(), 0, uncomp_len);
      }
      else
      {
        bytesLeft = uncomp_len;
        stream = new DeflateStream(baseStream, CompressionMode.Decompress, true);
//        compStream.Capacity = uncomp_len;
  //      ReadFully(ds, compStream.GetBuffer(), 0, uncomp_len);
    //    ds.Dispose();
      }
    }

    public void SendPacket(MySqlPacket packet)
    {
#if DUMP_PACKETS
      packet.Dump();
#endif
      byte[] buffer = packet.Buffer;
      int length = packet.Position - 4;

      if ((ulong)length > MaxPacketSize)
        throw new MySqlException(Resources.QueryTooLarge, (int)MySqlErrorCode.PacketTooLarge);

      int offset = 0;
      while (length > 0)
      {
        int lenToSend = length > MaxBlockSize ? MaxBlockSize : length;
        buffer[offset] = (byte)(lenToSend & 0xff);
        buffer[offset + 1] = (byte)((lenToSend >> 8) & 0xff);
        buffer[offset + 2] = (byte)((lenToSend >> 16) & 0xff);
        buffer[offset + 3] = SequenceByte++;

        stream.Write(buffer, offset, lenToSend + 4);
        stream.Flush();
        length -= lenToSend;
        offset += lenToSend;
      }
    }

    public void SendEntirePacketDirectly(byte[] buffer, int count)
    {
      buffer[0] = (byte)(count & 0xff);
      buffer[1] = (byte)((count >> 8) & 0xff);
      buffer[2] = (byte)((count >> 16) & 0xff);
      buffer[3] = SequenceByte++;
      stream.Write(buffer, 0, count + 4);
      stream.Flush();
    }

#endregion
  }
}
