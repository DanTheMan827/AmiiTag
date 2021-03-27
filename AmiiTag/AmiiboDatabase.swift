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
    
    static var lastUpdated: LastUpdated? {
        guard
            let jsonData = try? Data(contentsOf: lastUpdatedPath),
            let resultJson = LastUpdated.decodeJson(json: jsonData) else {
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
    public static let amiiboJsonPath = databasePath.appendingPathComponent("amiibo.json")
    public static let lastUpdatedPath = databasePath.appendingPathComponent("last-updated.json")
    public static let imagesPath = databasePath.appendingPathComponent("images")
    
    private static let lastUpdatedUrl = URL(string: "https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/last-updated.json")!
    private static let amiiboJsonUrl = URL(string: "https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/database/amiibo.json")!
    private static let imagesBase = URL(string: "https://raw.githubusercontent.com/N3evin/AmiiboAPI/master/images/")!
    
    public static func NeedsUpdate(completionHandler: @escaping (Result<Bool, Error>) -> Void) {
        if !FileManager.default.fileExists(atPath: amiiboJsonPath.path) {
            // No admiibo.json file
            completionHandler(.success(true))
            return
        }
        
        if !FileManager.default.fileExists(atPath: lastUpdatedPath.path) {
            // No last-updated.json
            completionHandler(.success(true))
            return
        }
        
        URLSession.shared.dataTask(with: lastUpdatedUrl) { (data, response, error) in
            if error != nil {
                completionHandler(.failure(error!))
            }
            
            guard let data = data,
                  let localLastUpdated = AmiiboDatabase.lastUpdated,
                  let remoteLastUpdated = LastUpdated.decodeJson(json: data) else {
                completionHandler(.failure(AmiiTagError(description: "Failed to decode last updated json")))
                return
            }
            
            if localLastUpdated.Timestamp != remoteLastUpdated.Timestamp {
                completionHandler(.success(true))
            } else {
                completionHandler(.success(false))
            }
        }.resume()
    }
    
    public static func UpdateDatabase(completionHandler: @escaping (StatusResult<Void, DownloadStatus, Error>) -> Void) {
        DownloadUrls(urls: [(remote: amiiboJsonUrl, local: amiiboJsonPath), (remote: lastUpdatedUrl, local: lastUpdatedPath)]) { result in
            switch result {
            case .success():
                LoadJson()
                var urls: [RemoteLocalUrlPair] = []
                for image in database.AmiiboData.keys.map({ (input) -> String in
                    return "icon_\(input.suffix(16).prefix(8))-\(input.suffix(8)).png"
                }) {
                    let localPath = imagesPath.appendingPathComponent(image)
                    
                    if !FileManager.default.fileExists(atPath: localPath.path) {
                        urls.append((remote: imagesBase.appendingPathComponent(image), local: localPath))
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
            try FileManager.default.createDirectory(at: databasePath, withIntermediateDirectories: true, attributes: nil)
            var resourceValues = URLResourceValues()
            resourceValues.isExcludedFromBackup = true
            var databasePath = AmiiboDatabase.databasePath
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
        }
    }
}
