﻿using System.Threading.Tasks;

namespace GatherMergeRewrite
{
    public interface IStorage
    {
        Task Save(string contentType, string name, string content);
        Task<string> Load(string name);
        string Container { get; set; }
        string BaseAddress { get; set; }
    }
}
