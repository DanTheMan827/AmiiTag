//
//  Extensions.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/10/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import CoreNFC

extension TagDump {
    var decryptedData: Data? {
        guard
            let decryptDataKeys = KeyFiles.dataKey?.derivedKey(uid: uid, writeCounter: data.subdata(in: 17..<19), salt: data.subdata(in: 96..<128)),
            let decryptedData = try? decryptDataKeys.decrypt(data.subdata(in: 20..<52) + data.subdata(in: 160..<520)) else {
                return nil
        }
        
        return decryptedData
    }
    
    var nickname: String {
        guard
            let decryptedData = decryptedData,
            let name = String(bytes: decryptedData[12..<32], encoding: .utf16BigEndian) else {
            return ""
        }
        
        if ((UInt16(decryptedData[2]) << 8) | UInt16(decryptedData[3])) == 0 {
            return ""
        }
        
        return name.trimmingCharacters(in: CharacterSet(["\0"]))
    }
    
    var displayName: String {
        let nickname = self.nickname
        if nickname.count > 0 {
            return nickname
        }
        
        if let name = self.amiiboName {
            return name
        }
        
        return "0x\(self.headHex)\(self.tailHex)"
    }
    
    var TagUIDSig: Data? {
        if self.data.count == 572 {
            return self.uid + self.data[9...9] + self.signature!
        }
        return nil
    }
    
    var amiiboInfo: AmiiboDatabase.AmiiboJsonData? {
        return AmiiboDatabase.database.AmiiboData["0x\(self.fullHex)"]
    }
    
    var amiiboName: String? {
        return amiiboInfo?.Name
    }
    
    var gameSeriesName: String? {
        if let name = self.amiiboName {
            if name.suffix(4) == "(N2)" {
                return nil
            }
            return AmiiboDatabase.database.GameSeries["0x\(self.gameSeriesHex)"]
        }
        return nil
    }
    
    var amiiboSeriesName: String? {
        if let name = self.amiiboName {
            if name.suffix(4) == "(N2)" {
                return nil
            }
            return AmiiboDatabase.database.AmiiboSeries["0x\(self.amiiboSeriesHex)"]
        }
        return nil
    }
    
    var typeName: String? {
        return AmiiboDatabase.database.Types["0x\(self.typeHex)"]
    }
    
    var image: UIImage? {
        return TagDump.GetImage(id: self.fullHex)
    }
}
