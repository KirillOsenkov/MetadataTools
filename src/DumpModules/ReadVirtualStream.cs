using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Runtime;

namespace Ref12.Diagnostics.Extraction
{
    internal class ReadVirtualStream : Stream
    {
        private byte[] _tmp;
        private long _pos;
        private long _disp;
        private long _len;
        private IDataReader _dataReader;

        public ReadVirtualStream(IDataReader dataReader, long displacement, long len)
        {
            _dataReader = dataReader;
            _disp = displacement;
            _len = len;
        }

        public override bool CanRead
        {
            get { return true; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override void Flush()
        {
        }

        public override long Length
        {
            get { return int.MaxValue; }
        }

        public override long Position
        {
            get
            {
                return _pos;
            }
            set
            {
                _pos = value;
                if (_pos > _len)
                    _pos = _len;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (offset == 0)
            {
                if (Read(buffer, count, out int read))
                    return read;

                return 0;
            }
            else
            {
                if (_tmp == null || _tmp.Length < count)
                    _tmp = new byte[count];

                if (!Read(_tmp, count, out int read))
                    return 0;

                Buffer.BlockCopy(_tmp, 0, buffer, offset, read);
                return read;
            }
        }

        private bool Read(byte[] buffer, int count, out int read)
        {
            if (_dataReader.ReadMemory((ulong)(_pos + _disp), buffer, count, out read))
            {
                _pos += read;
                return true;
            }

            return false;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    _pos = offset;
                    break;

                case SeekOrigin.End:
                    _pos = _len + offset;
                    if (_pos > _len)
                        _pos = _len;
                    break;

                case SeekOrigin.Current:
                    _pos += offset;
                    if (_pos > _len)
                        _pos = _len;
                    break;
            }

            return _pos;
        }

        public override void SetLength(long value)
        {
            _len = value;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new InvalidOperationException();
        }
    }
}
