//
//  AmiiboDatabase.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/7/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

public class AmiiboDatabase {
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
    
    fileprivate static var _database: AmiiboJson?
    
    public static var database: AmiiboJson {
        if _database != nil {
            return _database!
        }
        guard
            let jsonPath = try? Bundle.main.url(forResource: "amiibo", withExtension: "json"),
            let jsonData = try? Data(contentsOf: jsonPath),
            let resultJson = try? JSONDecoder().decode(AmiiboJson.self, from: jsonData) else {
                return AmiiboJson(AmiiboSeries: Dictionary<String, String>(), AmiiboData: Dictionary<String, AmiiboJsonData>(), Characters: Dictionary<String, String>(), GameSeries: Dictionary<String, String>(), Types: Dictionary<String, String>())
        }
        
        var newAmiiboData = resultJson.AmiiboData
        
        for (fake, real) in fakeAmiibo {
            if let realData = resultJson.AmiiboData["0x\(real)"] {
                
                newAmiiboData["0x\(fake)"] = AmiiboJsonData(Name: "\(realData.Name) (N2)", Release: realData.Release)
            }
        }
        
        _database = AmiiboJson(AmiiboSeries: resultJson.AmiiboSeries, AmiiboData: newAmiiboData, Characters: resultJson.Characters, GameSeries: resultJson.GameSeries, Types: resultJson.Types)
        
        return _database!
    }
    
    fileprivate static var _amiiboDumps: Dictionary<String, TagDump>?
    
    static var amiiboDumps: Dictionary<String, TagDump> {
        if _amiiboDumps != nil {
            return _amiiboDumps!
        }
        
        var dumps: [String : TagDump] = [:]
        let validSizes = [532, 540, 572]
        
        if let url = Bundle.main.url(forResource: "dumps", withExtension: nil) {
            let json = database
            if let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey], options: [.skipsPackageDescendants]) {
                for case let fileURL as URL in enumerator {
                    let path = fileURL.path
                    do {
                        let fileAttributes = try fileURL.resourceValues(forKeys:[.isRegularFileKey, .fileSizeKey])
                        if fileAttributes.isRegularFile! && validSizes.contains(fileAttributes.fileSize!) {
                            if let dump = try? TagDump(data: Data(contentsOf: fileURL)) {
                                if dumps["\(dump.headHex)\(dump.tailHex)"] == nil && json.AmiiboData["0x\(dump.headHex)\(dump.tailHex)"] != nil {
                                    dumps["\(dump.headHex)\(dump.tailHex)"] = dump
                                }
                            }
                        }
                    } catch { print(error, fileURL) }
                }
            }
        }
        
        _amiiboDumps = dumps
        return _amiiboDumps!
    }
    
}
