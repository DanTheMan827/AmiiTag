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

struct UidSigPair {
    let uid: Data
    let signature: Data
}

extension NFCMiFareTag {
    func checkPuck(completionHandler: @escaping (Result<Data, Error>) -> Void) {
        sendMiFareCommand(commandPacket: Data([0x3A, 133, 134])) { (data, error) in
            if let error = error {
                completionHandler(.failure(error))
            } else if data.elementsEqual([1, 2, 3, 4, 5, 6, 7, 8]) {
                completionHandler(.success(data))
            } else {
                completionHandler(.failure(NFCMiFareTagError.unknownError))
            }
        }
    }
}
extension NTAG215Tag {
    static let uidSignatures: [UidSigPair] = {
        var signatures: [UidSigPair] = []
        
        guard
            let signaturesPath = Bundle.main.url(forResource: "signatures", withExtension: "bin"),
            let signaturesData = try? Data(contentsOf: signaturesPath) else {
                return signatures
        }
        
        for index in stride(from: 0, to: signaturesData.count, by: 42) {
            if signaturesData[index + 9] == 0x48 {
                signatures.append(UidSigPair(uid: Data(signaturesData[index..<(index + 9)]), signature: Data(signaturesData[(index + 10)..<(index + 42)])))
            }
        }
        
        if signatures.count == 0 {
            signatures.append(UidSigPair(uid: Data([0x00, 0x00, 0x00, 0x88, 0x00, 0x00, 0x00, 0x00, 0x00]), signature: Data(count: 32)))
        }
        
        return signatures
    }()
}

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
        var imageFilename = "icon_\(self.headHex)-\(self.tailHex)"
        if let realId = AmiiboDatabase.fakeAmiibo["\(self.headHex)\(self.tailHex)"] {
            imageFilename = "icon_\(realId.prefix(8))-\(realId.suffix(8))"
        }
        
        if let imagePath = try? Bundle.main.path(forResource: imageFilename, ofType: "png", inDirectory: "images", forLocalization: nil),
            let image = UIImage(contentsOfFile: imagePath) {
            return image
        }
        
        return nil
    }
}

extension Data {
    func ToHex() -> String {
        return self.map { String(format: "%02hhx", $0) }.joined()
    }
}
