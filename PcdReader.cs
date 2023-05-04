// PCD file format: https://pcl.readthedocs.io/projects/tutorials/en/master/pcd_file_format.html#pcd-file-format
using System.Buffers;
using System.Runtime.CompilerServices;

class PcdReader
{
    private Header header;

    private Point3D[] points;

    private int pointCount = 0;

    public long FileSizeBytes { get; private set; }

    public int PointCount => pointCount;

    public Point3D[] Points => points;

    public Header Header => header;

    private const int MaxHeaderSize = 1024; // 1K

    public void ReadPcdFile(string fileName)
    {
        FileInfo fileInfo = new FileInfo(fileName);
        FileSizeBytes = fileInfo.Length;

        byte[] bytes;
        using (FileStream fileStream = File.OpenRead(fileName))
        using (BinaryReader binaryReader = new BinaryReader(fileStream))
        {
            bytes = binaryReader.ReadBytes(MaxHeaderSize);
        }

        ReadHeader(fileName, out var dataBytesIndex);

        string dataType = header.data.Split(' ')[1].TrimEnd();  //储存的数据类型，ascii binary binary_compressed

        var sizes = header.size
            .Split(' ')
            .Skip(1)
            .Select(item => int.Parse(item));
        var counts = header.count
            .Split(' ')
            .Skip(1)
            .Select(item => int.Parse(item));
        var rowSizeBytes = sizes
            .Zip(counts)
            .Sum(item => item.First * item.Second);
        pointCount = Convert.ToInt32(header.points.Split(' ')[1]);  //点的数量
        points = new Point3D[pointCount];

        switch (dataType)
        {
            case "binary":
                ReadBinaryData(fileName, bytes, rowSizeBytes, (int)dataBytesIndex, sizes);
                break;
            case "binary_compressed":
                ReadBinaryCompressedData(bytes, rowSizeBytes, (int)dataBytesIndex);
                break;
            case "ascii":
                throw new NotImplementedException("Sorry, ASCII data type is not currently supported.");
            default:
                break;
        }
    }

    private void ReadHeader(string fileName, out long dataBytesIndex)
    {
        var headerLines = new string[10];
        var newLineLengthBytes = LineBreakLengthBytes(fileName);

        using (var textReader = new StreamReader(fileName))
        {
            string? line;
            var headerLinesIndex = 0;
            dataBytesIndex = 0;

            do
            {
                line = textReader.ReadLine();

                if (line == null)
                {
                    throw new ArgumentException("PCD is not well-formed.");
                }

                dataBytesIndex += line.Length + newLineLengthBytes;

                if (line.StartsWith('#'))
                {
                    continue;
                }

                headerLines[headerLinesIndex] = line;
                headerLinesIndex++;
            }
            while (!line.StartsWith("DATA"));
        }

        header = new Header(headerLines);
    }

    private static int LineBreakLengthBytes(string fileName)
    {
        // We estimate \n (1 B) is by default; however, \r\n (2 B) could appear too
        int newLineBytesLength = 1;
        int peek;
        using var textReader = new StreamReader(fileName);

        while ((peek = textReader.Read()) >= 0)
        {
            var @char = (char)peek;

            if (@char == '\r')
            {
                peek = textReader.Read();
                @char = (char)peek;

                if (@char == '\n')
                {
                    newLineBytesLength = 2;
                    break;
                }
            }
            else if (@char == '\n')
            {
                newLineBytesLength = 1;
                break;
            }
        }

        return newLineBytesLength;
    }

    private void ReadBinaryCompressedData(byte[] bytes, int rowSizeBytes, int index)
    {
        Point3D point;
        //二进制压缩，先解析压缩前的数据量和压缩后数据量
        int[] bys = new int[2];
        int dataIndex = 0;
        for (int i = 0; i < bys.Length; i++)
        {
            bys[i] = BitConverter.ToInt32(bytes, index + i * 4);
            dataIndex = index + i * 4;
        }
        dataIndex += 4;  //数据开始的索引
        int compressedSize = bys[0];  //压缩之后的长度
        int decompressedSize = bys[1];  //解压之后的长度

        //将压缩后的数据单独拿出来
        byte[] compress = new byte[compressedSize];
        //将bs，从索引为a开始，复制到compress的[0至compressedSize]区间内
        Array.Copy(bytes, dataIndex, compress, 0, compressedSize);

        //LZF解压算法
        byte[] data = Decompress(compress, decompressedSize);
        int type = 0;
        var pointIndex = 0;

        for (int i = 0; i < data.Length; i += 4)
        {
            //先读取x坐标
            if (type == 0)
            {
                point = new Point3D();
                point.x = BitConverter.ToSingle(data, i);
                points[pointIndex] = point;

                if ((pointIndex + 1) == pointCount)
                {
                    type++;
                }

                pointIndex++;
            }
            else if (type == 1)  //y 坐标
            {
                var point3D = points[i / 4 - pointCount];
                point3D.y = BitConverter.ToSingle(data, i);
                points[i / 4 - pointCount] = point3D;
                if (i / 4 == pointCount * 2 - 1) type++;
            }
            else if (type == 2)   //z 坐标
            {
                var point3D = points[i / 4 - pointCount * 2];
                point3D.z = BitConverter.ToSingle(data, i);
                points[i / 4 - pointCount * 2] = point3D;
                if (i / 4 == pointCount * 3 - 1) type++;
            }
            else if (rowSizeBytes == 4)  //颜色信息
            {
                var point3D = points[i / 4 - pointCount * 3];
                point3D.colorRGB = BitConverter.ToUInt32(data, i);
                points[i / 4 - pointCount * 3] = point3D;
                if (i / 4 == pointCount * 4 - 1) break;
            }
        }
    }

    private void ReadBinaryData(string fileName, byte[] bytes, int rowSizeBytes, int index, IEnumerable<int> sizes)
    {
        var fields = header.fields
            .Split(' ')
            .Skip(1)
            .Select(item => item.TrimEnd())
            .ToList();
        var colorFieldIndex = fields.IndexOf("rgb");
        var xFieldIndex = fields.IndexOf("x");
        var xFieldOffset = 4 * xFieldIndex;
        var restOfFields = fields.Except(new string[] { "x", "y", "z", "rgb" });
        var isLabelAvailable = restOfFields.Contains("label");
        var labelOffset = GetFieldOffset("label", fields, sizes);
        var pointIndex = 0;


        int pointsToRead = 100000;

        using (FileStream fileStream = File.OpenRead(fileName))
        using (BinaryReader binaryReader = new BinaryReader(fileStream))
        {
            fileStream.Seek(index, SeekOrigin.Begin);

            int remainingPoints = this.pointCount;

            do
            {
                var loadPoints = Math.Min(pointsToRead, remainingPoints);
                int loadBufferSize = rowSizeBytes * loadPoints;
                remainingPoints -= loadPoints;

                bytes = binaryReader.ReadBytes(loadBufferSize);

                for (int byteId = 0; byteId < bytes.Length;)
                {
                    ref var point = ref points[pointIndex];
                    point.x = BitConverter.ToSingle(bytes, byteId + xFieldOffset);
                    point.y = BitConverter.ToSingle(bytes, byteId + xFieldOffset + 4);
                    point.z = BitConverter.ToSingle(bytes, byteId + xFieldOffset + 8);

                    if (colorFieldIndex >= 0)
                    {
                        point.colorRGB = BitConverter.ToUInt32(bytes, byteId + (4 * colorFieldIndex));
                    }

                    if (isLabelAvailable)
                    {
                        // TODO support types appart from byte
                        point.label = bytes[byteId + labelOffset];
                    }

                    if ((pointIndex + 1) == pointCount)
                    {
                        break;
                    }

                    byteId += rowSizeBytes;
                    pointIndex++;
                }

            } while (remainingPoints > 0);
        }
    }

    private int GetFieldOffset(string field, IList<string> fields, IEnumerable<int> sizes)
    {
        var fieldIndex = fields.IndexOf(field);
        var offset = sizes.Take(fieldIndex).Sum();

        return offset;
    }

    /// <summary>
    /// 使用LZF算法解压缩数据
    /// </summary>
    /// <param name="input">要解压的数据</param>
    /// <param name="outputLength">解压之后的长度</param>
    /// <returns>返回解压缩之后的内容</returns>
    private static byte[] Decompress(byte[] input, int outputLength)
    {
        uint iidx = 0;
        uint oidx = 0;
        int inputLength = input.Length;
        byte[] output = new byte[outputLength];
        do
        {
            uint ctrl = input[iidx++];

            if (ctrl < (1 << 5))
            {
                ctrl++;

                if (oidx + ctrl > outputLength)
                {
                    return null;
                }

                do
                    output[oidx++] = input[iidx++];
                while ((--ctrl) != 0);
            }
            else
            {
                var len = ctrl >> 5;
                var reference = (int)(oidx - ((ctrl & 0x1f) << 8) - 1);
                if (len == 7)
                    len += input[iidx++];
                reference -= input[iidx++];
                if (oidx + len + 2 > outputLength)
                {
                    return null;
                }
                if (reference < 0)
                {
                    return null;
                }
                output[oidx++] = output[reference++];
                output[oidx++] = output[reference++];
                do
                    output[oidx++] = output[reference++];
                while ((--len) != 0);
            }
        }
        while (iidx < inputLength);

        return output;
    }
}

/// <summary>
/// Pcd 中的点
/// </summary>
struct Point3D
{
    public float x;

    public float y;

    public float z;

    public uint colorRGB;

    public byte label;
}

/// <summary>
/// Pcd 文件的头部信息
/// </summary>
class Header
{
    public string version;

    public string fields;

    /// <summary>
    /// Size of each dimension (<see cref="fields"/>) in bytes.
    /// </summary>
    public string size;

    public string type;

    public string count;

    public string width;

    public string height;

    /// <summary>
    /// An acquisition viewpoint for the points in the dataset, 
    /// specified as a translation (tx ty tz) + quaternion (qw qx qy qz).
    /// </summary>
    public string viewpoint;

    public string points;

    public string data;

    public Header(params string[] headerLines)
    {
        version = headerLines[0];
        fields = headerLines[1];
        size = headerLines[2];
        type = headerLines[3];
        count = headerLines[4];
        width = headerLines[5];
        height = headerLines[6];
        viewpoint = headerLines[7];
        points = headerLines[8];
        data = headerLines[9];
        var viewpointParts = viewpoint.Split(' ').Skip(1);
        ViewpointTranslation = (
            viewpointParts.ElementAt(0),
            viewpointParts.ElementAt(1),
            viewpointParts.ElementAt(2));
        ViewpointQuaternion = (
            viewpointParts.ElementAt(3),
            viewpointParts.ElementAt(4),
            viewpointParts.ElementAt(5),
            viewpointParts.ElementAt(6));
    }

    public (string x, string y, string z) ViewpointTranslation { get; private set; }

    public (string w, string x, string y, string z) ViewpointQuaternion { get; private set; }
}
