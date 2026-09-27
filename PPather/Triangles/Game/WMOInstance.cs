/*
  This file is part of ppather.

    PPather is free software: you can redistribute it and/or modify
    it under the terms of the GNU Lesser General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    PPather is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU Lesser General Public License for more details.

    You should have received a copy of the GNU Lesser General Public License
    along with ppather.  If not, see <http://www.gnu.org/licenses/>.

    Copyright Pontus Borg 2008

 */

using PPather.Extensions;

using System;
using System.IO;
using System.Numerics;

namespace Wmo;

public readonly struct WMOInstance
{
    public readonly WMORoot wmo;
    public readonly int id;
    public readonly Vector3 pos, pos2, pos3;
    public readonly Vector3 dir;
    public readonly int d2; //d3
    public readonly int doodadset;
    public readonly float scale;

    public WMOInstance(BinaryReader file, WMORoot wmo)
    {
        // read X bytes from file
        this.wmo = wmo;

        id = file.ReadInt32();

        pos = file.ReadVector3();
        dir = file.ReadVector3();
        pos2 = file.ReadVector3();
        pos3 = file.ReadVector3();

        if (wmo.numericPlacement)
        {
            d2 = file.ReadUInt16(); // flags
            doodadset = file.ReadUInt16();
            _ = file.ReadUInt16(); // name set
            scale = file.ReadUInt16() / 1024f;
            if (scale <= 0)
                throw new InvalidDataException("Numeric WMO placement has zero scale.");
        }
        else
        {
            d2 = file.ReadInt32();
            doodadset = file.ReadInt16();
            file.BaseStream.Seek(sizeof(Int16), SeekOrigin.Current);
            scale = 1;
        }
    }
}
