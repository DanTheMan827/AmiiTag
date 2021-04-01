//
//  AmiiboDatabase.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/7/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

public class AmiiboDatabase {
    public typealias RemoteLocalUrlPair = (remote: URL, local: URL)
    public typealias DownloadStatus = (total: Int, remaining: Int, url: RemoteLocalUrlPair)
    public typealias Sha1sum = Dictionary<String, String>
    
    public struct AmiiboJsonData: Codable {
        public let Name: String
        public let Release: [String: String?]
        
        public enum CodingKeys: String, CodingKey {
            case Name = "name"
            case Release = "release"
        }
    }
    
    public struct AmiiboJson: Codable {
        public let AmiiboSeries: [String: String]
        public let AmiiboData: [String: AmiiboJsonData]
        public let Characters: [String: String]
        public let GameSeries: [String: String]
        public let Types: [String: String]
        
        public enum CodingKeys: String, CodingKey {
            case AmiiboSeries = "amiibo_series"
            case AmiiboData = "amiibos"
            case Characters = "characters"
            case GameSeries = "game_series"
            case Types = "types"
        }
        
        public static func GetEmptyData() -> AmiiboJson {
            return AmiiboJson(AmiiboSeries: [:], AmiiboData: [:], Characters: [:], GameSeries: [:], Types: [:])
        }
        
        public static func decodeJson(json: Data) -> AmiiboJson? {
            guard let resultJson = try? JSONDecoder().decode(AmiiboJson.self, from: json) else {
                return nil
            }
            
            return resultJson
        }
    }
    
    static func decodeSha1sumJson(json: Data) -> Sha1sum? {
        guard let resultJson = try? JSONDecoder().decode(Sha1sum.self, from: json) else {
            return nil
        }
        
        return resultJson
    }
    
    public struct LastUpdated: Codable {
        public let AmiiboSha1: String
        public let GameInfoSha1: String
        public let Timestamp: String
        
        public enum CodingKeys: String, CodingKey {
            case AmiiboSha1 = "amiibo_sha1"
            case GameInfoSha1 = "game_info_sha1"
            case Timestamp = "timestamp"
        }
        
        public static func decodeJson(json: Data) -> LastUpdated? {
            guard let resultJson = try? JSONDecoder().decode(LastUpdated.self, from: json) else {
                return nil
            }
            
            return resultJson
        }
    }
    
    static var sha1sum: Sha1sum? {
        guard
            let jsonData = try? Data(contentsOf: Sha1sumPath),
            let resultJson = decodeSha1sumJson(json: jsonData) else {
            return nil
        }
        
        return resultJson
    }
    
    static let fakeAmiibo: Dictionary<String, String> = [
        "0741000000000002": "0741000000200002", // Dark Pit (SSB)
        "0008000000000002": "0008000000030002", // Donkey Kong (SSB)
        "0581000000000002": "05810000001c0002", // Falco (SSB)
        "2281000000000002": "2281000002510002", // Lucas (SSB)
        "0742000000000002": "07420000001f0002", // Palutena (SSB)
        "1919000000000002": "1919000000090002", // Pikachu (SSB)
        "0781000000000002": "0781000000330002", // R.O.B (SSB)
        "0003000000000002": "0003000000020002", // Yoshi (SSB)
        "0100000000000002": "0100000000040002", // Link (SSB)
        "34c0000000000002": "34c0000002530002", // Ryu (SSB)
        "2104000000000002": "2104000002520002"  // Roy (SSB)
    ]
    
    public static var database: AmiiboJson = AmiiboJson.GetEmptyData()
    public static let databasePath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Database")
    public static let apiPath = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("AmiiboAPI")
    public static let amiiboJsonPath = apiPath.appendingPathComponent("database").appendingPathComponent("amiibo.json")
    public static let gamesInfoJsonPath = apiPath.appendingPathComponent("database").appendingPathComponent("games_info.json")
    public static let Sha1sumPath = apiPath.appendingPathComponent("sha1sum.json")
    public static let imagesPath = apiPath.appendingPathComponent("images")
    
    private static let sha1sumUrl = URL(string: "https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/sha1sum.json")!
    private static let apiBase = URL(string: "https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/")!
    
    public static func NeedsUpdate(completionHandler: @escaping (Result<Bool, Error>) -> Void) {
        if !FileManager.default.fileExists(atPath: amiiboJsonPath.path) {
            // No admiibo.json file
            completionHandler(.success(true))
            return
        }
        
        if !FileManager.default.fileExists(atPath: Sha1sumPath.path) {
            // No sha1sum.json
            completionHandler(.success(true))
            return
        }
        
        URLSession.shared.dataTask(with: sha1sumUrl) { (data, response, error) in
            if error != nil {
                completionHandler(.failure(error!))
            }
            
            guard let data = data,
                  let localSha1sum = AmiiboDatabase.sha1sum,
                  let remoteSha1sum = decodeSha1sumJson(json: data) else {
                completionHandler(.failure(AmiiTagError(description: "Failed to decode sha1sum.json")))
                return
            }
            
            if NSDictionary(dictionary: localSha1sum).isEqual(to: remoteSha1sum) {
                completionHandler(.success(false))
            } else {
                completionHandler(.success(true))
            }
        }.resume()
    }
    
    public static func UpdateDatabase(completionHandler: @escaping (StatusResult<Void, DownloadStatus, Error>) -> Void) {
        DownloadUrls(urls: [(remote: sha1sumUrl, local: Sha1sumPath)]) { result in
            DispatchQueue.main.async {
                switch result {
                case .success():
                    var urls: [RemoteLocalUrlPair] = []
                    let sha1sum = AmiiboDatabase.sha1sum!
                    
                    for hash in sha1sum {
                        let localPath = apiPath.appendingPathComponent(hash.key)
                        
                        if FileManager.default.fileExists(atPath: localPath.path),
                           localPath.sha1()?.toHexString().lowercased() == hash.value.lowercased() {
                            // The file exists and the hash matches
                        } else {
                            urls.append((remote: apiBase.appendingPathComponent(hash.key), local: localPath))
                        }
                    }
                    
                    if urls.count > 0 {
                        DownloadUrls(urls: urls, completionHandler: completionHandler)
                    } else {
                        completionHandler(.success(()))
                    }
                    
                default: completionHandler(result)
                }
            }
        }
    }
    
    public static func DownloadUrls(urls: [RemoteLocalUrlPair], total: Int? = nil, completionHandler: @escaping (StatusResult<Void, DownloadStatus, Error>) -> Void) {
        if urls.count == 0 {
            completionHandler(.success(()))
        } else {
            var urls = urls
            let total = total ?? urls.count
            let url = urls.removeFirst()
            
            completionHandler(.status((total: total, remaining: urls.count, url: url)))
            URLSession.shared.downloadTask(with: url.remote) { localURL, urlResponse, error in
                if error != nil {
                    completionHandler(.failure(error!))
                    return
                }
                
                guard let localURL = localURL else {
                    completionHandler(.failure(AmiiTagError(description: "Local URL is nil")))
                    return
                }
                
                do {
                    try FileManager.default.createDirectory(at: url.local.deletingLastPathComponent(), withIntermediateDirectories: true)
                    
                    if FileManager.default.fileExists(atPath: url.local.path) {
                        try FileManager.default.removeItem(atPath: url.local.path)
                    }
                    
                    try FileManager.default.moveItem(atPath: localURL.path, toPath: url.local.path)
                    
                    DownloadUrls(urls: urls, total: total, completionHandler: completionHandler)
                } catch {
                    completionHandler(.failure(error))
                }
            }.resume()
        }
    }
    
    public static func LoadJson(){
        do {
            if FileManager.default.fileExists(atPath: databasePath.path) && !FileManager.default.fileExists(atPath: apiPath.path) {
                try FileManager.default.moveItem(at: databasePath, to: apiPath)
            }
            
            try FileManager.default.createDirectory(at: apiPath, withIntermediateDirectories: true, attributes: nil)
            var resourceValues = URLResourceValues()
            resourceValues.isExcludedFromBackup = true
            var databasePath = AmiiboDatabase.apiPath
            try databasePath.setResourceValues(resourceValues)
        } catch {
            print(error)
        }
        
        if FileManager.default.fileExists(atPath: amiiboJsonPath.path) {
            guard
                let jsonData = try? Data(contentsOf: amiiboJsonPath),
                let resultJson = AmiiboJson.decodeJson(json: jsonData) else {
                database = AmiiboJson.GetEmptyData()
                
                return
            }
            
            var newAmiiboData = resultJson.AmiiboData
            
            for (fake, real) in fakeAmiibo {
                if let realData = resultJson.AmiiboData["0x\(real)"] {
                    
                    newAmiiboData["0x\(fake)"] = AmiiboJsonData(Name: "\(realData.Name) (N2)", Release: realData.Release)
                }
            }
            
            database = AmiiboJson(AmiiboSeries: resultJson.AmiiboSeries, AmiiboData: newAmiiboData, Characters: resultJson.Characters, GameSeries: resultJson.GameSeries, Types: resultJson.Types)
            
            print("Loaded \(AmiiboDatabase.database.AmiiboData.count) amiibo dumps")
        }
    }
}
