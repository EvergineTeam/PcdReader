﻿using System.Text;
using System.Text.RegularExpressions;

// PCD file format: https://pcl.readthedocs.io/projects/tutorials/en/master/pcd_file_format.html#pcd-file-format
class PcdReader
{
    Header header;
    List<Point3D> point3Ds;
    int pointCount = 0;

    public int FileSizeBytes { get; private set; }

    public int PointCount => pointCount;

    public IEnumerable<Point3D> Points => point3Ds;

    public (string x, string y, string z) ViewpointTranslation { get; private set; }

    public (string w, string x, string y, string z) ViewpointQuaternion { get; private set; }

    /// <summary>
    /// 获取头部信息
    /// </summary>
    /// <param name="s"></param>
    private void GetHeader(string s)
    {
        header = new Header();
        Regex reg_VERSION = new Regex("VERSION .*");  //版本
        Regex reg_FIELDS = new Regex("FIELDS .*");  //字段
        Regex reg_SIZE = new Regex("SIZE .*");  //数据大小
        Regex reg_TYPE = new Regex("TYPE .*");  //存储数据的格式 F-Float U-uint
        Regex reg_COUNT = new Regex("COUNT .*");
        Regex reg_WIDTH = new Regex("WIDTH .*");
        Regex reg_HEIGHT = new Regex("HEIGHT .*");
        Regex reg_VIEWPOINT = new Regex("VIEWPOINT .*");
        Regex reg_POINTS = new Regex("POINTS .*");  // Number of points
        Regex reg_DATA = new Regex("DATA .*");  //数据类型

        Match m_VERSION = reg_VERSION.Match(s);
        header.VERSION = m_VERSION.Value;

        header.FistLine = "# .PCD v" + header.VERSION.Split(' ')[1] + " - Point Cloud Data file format";

        Match m_FIELDS = reg_FIELDS.Match(s);
        header.FIELDS = m_FIELDS.Value;

        Match m_SIZE = reg_SIZE.Match(s);
        header.SIZE = m_SIZE.Value;

        Match m_TYPE = reg_TYPE.Match(s);
        header.TYPE = m_TYPE.Value;

        Match m_COUNT = reg_COUNT.Match(s);
        header.COUNT = m_COUNT.Value;

        Match m_WIDTH = reg_WIDTH.Match(s);
        header.WIDTH = m_WIDTH.Value;

        Match m_HEIGHT = reg_HEIGHT.Match(s);
        header.HEIGHT = m_HEIGHT.Value;

        Match m_VIEWPOINT = reg_VIEWPOINT.Match(s);
        header.VIEWPOINT = m_VIEWPOINT.Value;
        var viewpointParts = header.VIEWPOINT.Split(' ').Skip(1);
        ViewpointTranslation = (
            viewpointParts.ElementAt(0),
            viewpointParts.ElementAt(1),
            viewpointParts.ElementAt(2));
        ViewpointQuaternion = (
            viewpointParts.ElementAt(3),
            viewpointParts.ElementAt(4),
            viewpointParts.ElementAt(5),
            viewpointParts.ElementAt(6));

        Match m_POINTS = reg_POINTS.Match(s);
        header.POINTS = m_POINTS.Value;

        Match m_DATA = reg_DATA.Match(s);
        header.DATA = m_DATA.Value;
    }

    /// <summary>
    /// 读取 binary 和 binary_compressed 格式的 pcd 文件
    /// </summary>
    public void ReadPcdFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);  //读取文件到字节数组
        FileSizeBytes = bytes.Length;
        string text = Encoding.UTF8.GetString(bytes);  //转为文本，方便分离文件头部信息

        GetHeader(text);

        string dataType = header.DATA.Split(' ')[1].TrimEnd();  //储存的数据类型，ascii binary binary_compressed

        var sizes = header.SIZE
            .Split(' ')
            .Skip(1)
            .Select(item => int.Parse(item));
        var counts = header.COUNT
            .Split(' ')
            .Skip(1)
            .Select(item => int.Parse(item));
        var rowSizeBytes = sizes
            .Zip(counts)
            .Sum(item => item.First * item.Second);
        pointCount = Convert.ToInt32(header.POINTS.Split(' ')[1]);  //点的数量
        int index = text.IndexOf(header.DATA) + header.DATA.Length + 1;  //数据开始的索引
        point3Ds = new List<Point3D>();

        switch (dataType)
        {
            case "binary":
                ReadBinaryData(bytes, rowSizeBytes, index, sizes);
                break;
            case "binary_compressed":
                ReadBinaryCompressedData(bytes, rowSizeBytes, index);
                break;
            case "ascii":
                throw new NotImplementedException("Sorry, ASCII data type is not currently supported.");
            default:
                break;
        }
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
        for (int i = 0; i < data.Length; i += 4)
        {
            //先读取x坐标
            if (type == 0)
            {
                point = new Point3D();
                point.x = BitConverter.ToSingle(data, i);
                point3Ds.Add(point);
                if (point3Ds.Count == pointCount) type++;
            }
            else if (type == 1)  //y 坐标
            {
                point3Ds[i / 4 - pointCount].y = BitConverter.ToSingle(data, i);
                if (i / 4 == pointCount * 2 - 1) type++;
            }
            else if (type == 2)   //z 坐标
            {
                point3Ds[i / 4 - pointCount * 2].z = BitConverter.ToSingle(data, i);
                if (i / 4 == pointCount * 3 - 1) type++;
            }
            else if (rowSizeBytes == 4)  //颜色信息
            {
                point3Ds[i / 4 - pointCount * 3].colorRGB = BitConverter.ToUInt32(data, i);
                if (i / 4 == pointCount * 4 - 1) break;
            }
        }
    }

    private void ReadBinaryData(byte[] bytes, int rowSizeBytes, int index, IEnumerable<int> sizes)
    {
        var fields = header.FIELDS
            .Split(' ')
            .Skip(1)
            .Select(item => item.TrimEnd())
            .ToList();
        var colorFieldIndex = fields.IndexOf("rgb");
        var xFieldIndex = fields.IndexOf("x");
        var restOfFields = fields.Except(new string[] { "x", "y", "z", "rgb" });
        Point3D point;
        
        for (int i = index; i < bytes.Length;)
        {
            point = new Point3D();
            point.x = BitConverter.ToSingle(bytes, i + (4 * xFieldIndex));
            point.y = BitConverter.ToSingle(bytes, i + (4 * xFieldIndex) + 4);
            point.z = BitConverter.ToSingle(bytes, i + (4 * xFieldIndex) + 8);
            if (colorFieldIndex >= 0)
            {
                point.colorRGB = BitConverter.ToUInt32(bytes, i + (4 * colorFieldIndex));
            }

            if (restOfFields.Any())
            { 
                var otherField = restOfFields.First();
                point.restOfFields = new Dictionary<string, object>(restOfFields.Count());
                var offset = GetFieldOffset(otherField, fields, sizes);
                // TODO support types appart from byte
                point.restOfFields[otherField] = bytes[i + offset];
            }

            point3Ds.Add(point);
            if (point3Ds.Count == pointCount) break;
            i += rowSizeBytes;
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
    public static byte[] Decompress(byte[] input, int outputLength)
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
class Point3D
{
    public float x;
    public float y;
    public float z;
    public uint colorRGB;
    public Dictionary<string, object>? restOfFields;
}

/// <summary>
/// Pcd 文件的头部信息
/// </summary>
class Header
{
    public string FistLine = "";  //pcd 文件的第一行
    public string VERSION;
    public string FIELDS;
    /// <summary>
    /// Size of each dimension (<see cref="FIELDS"/>) in bytes.
    /// </summary>
    public string SIZE;
    public string TYPE;
    public string COUNT;
    public string WIDTH;
    public string HEIGHT;
    /// <summary>
    /// An acquisition viewpoint for the points in the dataset, 
    /// specified as a translation (tx ty tz) + quaternion (qw qx qy qz).
    /// </summary>
    public string VIEWPOINT;
    public string POINTS;
    public string DATA;
}
