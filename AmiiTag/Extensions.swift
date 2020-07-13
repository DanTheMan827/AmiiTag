//
//  Extensions.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 7/10/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation

struct UidSigPair {
    let uid: Data
    let signature: Data
}

extension NTAG215Tag {
    fileprivate static var _uidSignatures: [UidSigPair]?
    static var uidSignatures: [UidSigPair] {
        if _uidSignatures != nil {
            return _uidSignatures!
        }
        
        var signatures: [UidSigPair] = []
        
        guard
            let signaturesPath = Bundle.main.url(forResource: "signatures", withExtension: "bin"),
            let signaturesData = try? Data(contentsOf: signaturesPath) else {
                return signatures
        }
        
        for index in stride(from: 0, to: signaturesData.count, by: 42) {
            if signaturesData[index + 9] == 0x48 {
                signatures.append(UidSigPair(uid: signaturesData[index..<(index + 9)], signature: signaturesData[(index + 10)..<(index + 42)]))
            }
        }
        
        _uidSignatures = signatures
        return signatures
    }
}

extension TagDump {
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
}

extension Data {
    func ToHex() -> String {
        return self.map { String(format: "%02hhx", $0) }.joined()
    }
}
