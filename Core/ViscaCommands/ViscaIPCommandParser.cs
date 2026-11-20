using PtzJoystickControl.Core.Devices;

namespace PtzJoystickControl.Core.ViscaCommands;

public static class ViscaIPCommandParser
{
    public static void ParseReply(ViscaIPDeviceBase viscaIPDevice, byte[] buffer, int length, int startIndex = 0)
    {
        int endIndex = startIndex + length;
        if (0 > startIndex || buffer.Length < endIndex)
            throw new ArgumentOutOfRangeException("index + length");

        // Many cameras (Tenveo and others) answer with plain VISCA, without the 8-byte VISCA-over-IP header,
        // even when the command had one. Plain replies start with 0x90..0xF0; headered ones with 0x01 or 0x02.
        if (IsRawViscaReply(buffer, startIndex, length))
        {
            ParseRawReplies(viscaIPDevice, buffer, startIndex, length);
            return;
        }

        if (length < 9) throw new ArgumentException("Packet min length is 9 bytes.");

        int headerType = buffer[startIndex++] << 8 | buffer[startIndex++];
        int commandLength = buffer[startIndex++] << 8 | buffer[startIndex++];
        int packetId = buffer[startIndex++] << 24 
            | buffer[startIndex++] << 16 
            | buffer[startIndex++] << 8 
            | buffer[startIndex++];

        if(commandLength != length - 8)
            throw new ArgumentException($"Packet length mismatch. Header: {commandLength} - Actual: {length - 8}");

        startIndex++; //Skip address byte as it's locked in ViscaIP
        switch (headerType)
        {
            case (ushort)ViscaIPHeaderType.CommandReply:
                ViscaCommandParser.ParseReply(viscaIPDevice, buffer, startIndex);
                break;
            case (ushort)ViscaIPHeaderType.ControlReply:
                break;
            case (ushort)ViscaIPHeaderType.Command:
            case (ushort)ViscaIPHeaderType.Inquery:
            case (ushort)ViscaIPHeaderType.SettingCommand:
            case (ushort)ViscaIPHeaderType.ControlCommand:
                throw new Exception("Not a reply type header");
            default:
                throw new Exception("Invalid header type");
        }

        viscaIPDevice.remotePacketId = packetId;
    }

    /// <summary>True when the datagram is plain VISCA (for example 90 41 FF) instead of a VISCA-over-IP packet.</summary>
    public static bool IsRawViscaReply(byte[] buffer, int startIndex, int length) =>
        length >= 3 && (buffer[startIndex] & 0x80) != 0 && buffer[startIndex + length - 1] == 0xFF;

    // One datagram can hold several messages, for example Ack followed by Completion: 90 41 FF 90 51 FF.
    private static void ParseRawReplies(ViscaIPDeviceBase device, byte[] buffer, int startIndex, int length)
    {
        int end = startIndex + length;
        int messageStart = startIndex;
        for (int i = startIndex; i < end; i++)
        {
            if (buffer[i] != 0xFF)
                continue;
            // Skip the address byte (90), as the headered path does: the parser expects the reply type next.
            ViscaCommandParser.ParseReply(device, buffer, messageStart + 1);
            messageStart = i + 1;
        }
    }
}
