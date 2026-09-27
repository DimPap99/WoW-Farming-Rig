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

using StormDll;
using PPather.Storage;

using System;
using System.IO;
using System.Collections.Generic;

namespace Wmo;

public sealed class ModelManager : Manager<Model>
{
    private readonly IGameFileSource archive;
    private readonly Dictionary<uint, Model> fileDataIdModels = new();
    private readonly Dictionary<uint, Model> collisionModels = new();
    private readonly Dictionary<string, Model> collisionModelsByPath = new(StringComparer.OrdinalIgnoreCase);

    public ModelManager(IGameFileSource archive)
    {
        this.archive = archive;
    }

    public override bool Load(ReadOnlySpan<char> path, out Model t)
    {
        ReadOnlySpan<char> extension = Path.GetExtension(path);

        if (extension.SequenceEqual(".mdx") || extension.SequenceEqual(".mdl") ||
            extension.SequenceEqual(".MDX") || extension.SequenceEqual(".MDL"))
        {
            Span<char> mutablePath = stackalloc char[path.Length - 1];
            path[..^1].CopyTo(mutablePath);

            mutablePath[^1] = '2';
            t = ModelFile.Read(archive, mutablePath);
            return true;
        }

        t = ModelFile.Read(archive, path);
        return true;
    }

    public Model AddAndLoadIfNeeded(uint fileDataId)
    {
        if (!fileDataIdModels.TryGetValue(fileDataId, out Model model))
        {
            model = ModelFile.Read(archive, fileDataId);
            fileDataIdModels.Add(fileDataId, model);
        }

        return model;
    }

    public Model LoadCollisionIfNeeded(uint fileDataId)
    {
        if (!collisionModels.TryGetValue(fileDataId, out Model model))
        {
            model = ModelFile.ReadCollision(archive, fileDataId);
            collisionModels.Add(fileDataId, model);
        }
        return model;
    }

    public Model LoadCollisionIfNeeded(string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Equals(".mdx", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase))
            path = Path.ChangeExtension(path, ".m2");

        if (!collisionModelsByPath.TryGetValue(path, out Model model))
        {
            model = ModelFile.ReadCollision(archive, path);
            collisionModelsByPath.Add(path, model);
        }
        return model;
    }

    public new void Clear()
    {
        base.Clear();
        fileDataIdModels.Clear();
        collisionModels.Clear();
        collisionModelsByPath.Clear();
    }
}
