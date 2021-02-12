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
    
    public static let database: AmiiboJson = {
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
        
        return AmiiboJson(AmiiboSeries: resultJson.AmiiboSeries, AmiiboData: newAmiiboData, Characters: resultJson.Characters, GameSeries: resultJson.GameSeries, Types: resultJson.Types)
    }()
    
    static let amiiboDumps: Dictionary<String, TagDump> = {
        var dumps: [String : TagDump] = [:]
        
        guard
            let amiiboPath = Bundle.main.url(forResource: "amiibo", withExtension: "bin"),
            let amiiboData = try? Data(contentsOf: amiiboPath) else {
                return dumps
        }
        
        let json = database
        
        
        json.AmiiboData.keys.forEach { (key) in
            if fakeAmiibo[String(key.suffix(16))] == nil {
                var ID = key.suffix(16)
                var dataId = Data.fromHexString(hex: String(ID))
                var salt = Data(count: 32)
                let result = salt.withUnsafeMutableBytes {
                    SecRandomCopyBytes(kSecRandomDefault, 32, $0.baseAddress!)
                }
                
                var dumpData = Data()
                
                dumpData.append(Data([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x48, 0x0f, 0xe0, 0xf1, 0x10, 0xff, 0xee, 0xa5, 0x00, 0x00, 0x00]))
                dumpData.append(Data(count: 64))
                dumpData.append(dataId)
                dumpData.append(Data(count: 4))
                dumpData.append(salt)
                dumpData.append(Data(count: 392))
                dumpData.append(Data([0x01, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x00, 0x04, 0x5F, 0x00, 0x00, 0x00]))
                
                if let dump = try? TagDump(data: dumpData) {
                    dumps[String(ID)] = dump
                }
            }
        }
        
        /*
        for index in stride(from: 0, to: amiiboData.count, by: 532) {
            if let dump = try? TagDump(data: Data(amiiboData[index..<(index + 532)])) {
                if dumps["\(dump.headHex)\(dump.tailHex)"] == nil && json.AmiiboData["0x\(dump.headHex)\(dump.tailHex)"] != nil && fakeAmiibo[dump.fullHex] == nil {
                    dumps["\(dump.headHex)\(dump.tailHex)"] = dump
                }
            }
        }
        */
        
        return dumps
    }()
}
