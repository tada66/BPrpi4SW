internal static class Cobs
{
    internal static byte[] Encode(byte[] data)
    {
        var output = new List<byte>();
        int codeIdx = 0;
        byte code = 1;
        output.Add(0); // Placeholder for first code byte

        foreach (byte b in data)
        {
            if (b == 0)
            {
                output[codeIdx] = code;
                code = 1;
                codeIdx = output.Count;
                output.Add(0); // Placeholder for next code byte
            }
            else
            {
                output.Add(b);
                code++;
                if (code == 0xFF)
                {
                    output[codeIdx] = code;
                    code = 1;
                    codeIdx = output.Count;
                    output.Add(0);
                }
            }
        }
        output[codeIdx] = code;
        return output.ToArray();
    }

    internal static byte[] Decode(byte[] data)
    {
        var output = new List<byte>();
        int idx = 0;

        while (idx < data.Length)
        {
            byte code = data[idx++];
            if (code == 0) break; // Invalid

            for (int i = 1; i < code; i++)
            {
                if (idx >= data.Length) break;
                output.Add(data[idx++]);
            }

            if (code < 0xFF && idx < data.Length)
            {
                output.Add(0);
            }
        }
        return output.ToArray();
    }
}